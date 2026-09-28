using System.Runtime.Serialization;
using System.Text;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.UI;

namespace OwnBridge;

[DataContract]
internal sealed class ChatPanelData : NotifyPropertyChangedObject, IDisposable
{
    private const string Header = "OwnBridge — Phase 6";

    private readonly VisualStudioExtensibility extensibility;
    private readonly CodexChatProvider chatGpt = new();
    private readonly GeminiChatProvider gemini = new();
    private readonly SemaphoreSlim approvalGate = new(1, 1);
    private IChatProvider? runningProvider;
    private CancellationTokenSource? turnCancel;
    private ConversationSession? session;
    private TaskPlan? plan;
    private HashSet<string>? taskChangedPaths;
    private TaskCompletionSource<bool>? pendingApproval;
    private string? pendingDiff;

    private bool isChatGpt = true;
    private bool isGemini;
    private string connectLabel = "Connect ChatGPT";
    private string headerText = "Phase 6 · ChatGPT";
    private string accountText = "Account: checking...";
    private string usageText = string.Empty;
    private bool showHistory;
    private string geminiKeyInput = string.Empty;
    private string modelInput = "default";
    private string prompt = string.Empty;
    private string transcript = Header + "\n\nChoose ChatGPT or Gemini, open a file from your solution, then ask about it, ask for a code change, " +
                                "or attach a plan / Excel issue list and run it task by task.";
    private string status = "Checking connection...";
    private string workspaceText = "Workspace: open a file from your solution";
    private bool includeEditorContext = true;
    private bool busy;
    private bool isRunning;
    private bool hasApproval;
    private bool hasDiff;
    private bool approvalDeletes;
    private string approvalTitle = string.Empty;
    private string approvalDetail = string.Empty;
    private string attachPath = string.Empty;
    private bool hasTasks;
    private string taskHeader = string.Empty;
    private bool autoContinue;
    private bool commitEachTask = true;

    public ChatPanelData(VisualStudioExtensibility extensibility)
    {
        this.extensibility = extensibility;
        chatGpt.Progress = update => Status = update;
        gemini.Progress = update => Status = update;

        ConnectCommand = new AsyncCommand(async (_, cancellationToken) =>
        {
            if (busy) return;
            Busy = true;
            var provider = SelectedProvider;
            try
            {
                Status = $"Checking {provider.DisplayName} sign-in...";
                Status = await provider.ConnectAsync(update => Status = update, cancellationToken);
            }
            catch (Exception ex)
            {
                Status = $"Connection failed: {ex.Message}";
            }
            finally
            {
                Transcript = (session?.RenderTranscript() ?? Header) + $"\n\nOwnBridge: {Status}";
                Busy = false;
                _ = RefreshInfoAsync();
                _ = RefreshModelsAsync();
            }
        });

        SendCommand = new AsyncCommand(async (parameter, clientContext, cancellationToken) =>
        {
            if (busy) return;
            var message = (parameter as string)?.Trim();
            if (string.IsNullOrWhiteSpace(message)) return;

            Busy = true;
            try
            {
                var editor = await EditorContext.CaptureAsync(clientContext, cancellationToken);
                var current = EnsureSession(editor);
                if (current is null)
                {
                    Status = "Open any file from your solution in the editor, then press Send again.";
                    return;
                }
                Prompt = string.Empty;
                await RunTurnAsync(current, SelectedProvider, message, message, includeEditorContext ? editor : null,
                    editor?.FilePath, cancellationToken);
            }
            finally
            {
                Busy = false;
            }
        });

        StopCommand = new AsyncCommand(async (_, cancellationToken) =>
        {
            if (!isRunning) return;
            AutoContinue = false;
            ClearApproval(approved: false);
            Status = "Stopping...";
            try { if (runningProvider is { } active) await active.StopAsync(cancellationToken); }
            catch (Exception ex) { Status = $"Could not stop cleanly: {ex.Message}"; }
            // Give the engine a moment to finish on its own, then cancel the wait in OwnBridge.
            await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);
            try { turnCancel?.Cancel(); } catch (ObjectDisposedException) { }
        });

        ApproveCommand = new AsyncCommand((_, _) => { ClearApproval(approved: true); return Task.CompletedTask; });
        DeclineCommand = new AsyncCommand((_, _) => { ClearApproval(approved: false); return Task.CompletedTask; });

        ViewDiffCommand = new AsyncCommand(async (_, cancellationToken) =>
        {
            var diff = pendingDiff;
            if (diff is null) return;
            try
            {
                var folder = Path.Combine(Path.GetTempPath(), "OwnBridge");
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, $"change-{DateTime.Now:yyyyMMdd-HHmmss}.diff");
                await File.WriteAllTextAsync(file, diff, cancellationToken);
                await extensibility.Documents().OpenDocumentAsync(new Uri(file), cancellationToken);
            }
            catch (Exception ex)
            {
                Status = $"Could not open the diff: {ex.Message}";
            }
        });

        NewChatCommand = new AsyncCommand((_, _) =>
        {
            if (busy) return Task.CompletedTask;
            if (session is null)
            {
                Status = "Open a file from your solution first; the new chat belongs to that solution.";
                return Task.CompletedTask;
            }
            var workspace = session.Workspace;
            SwitchTo(ConversationSession.CreateNew(workspace));
            Transcript = session!.RenderTranscript() + "\n\nNew chat started.";
            ShowHistory = false;
            return Task.CompletedTask;
        });

        ToggleHistoryCommand = new AsyncCommand((_, _) =>
        {
            if (session is null)
            {
                Status = "Open a file from your solution and send a message; history is kept per solution.";
                return Task.CompletedTask;
            }
            RefreshHistory();
            ShowHistory = !showHistory;
            return Task.CompletedTask;
        });

        SaveGeminiKeyCommand = new AsyncCommand((_, _) =>
        {
            var key = geminiKeyInput.Trim();
            GeminiKeyInput = string.Empty;
            if (key.Length < 10)
            {
                Status = "That does not look like a Gemini API key.";
                return Task.CompletedTask;
            }
            try
            {
                gemini.SetApiKey(key);
                session?.ClearThread("gemini");
                Status = $"Saved. Gemini now uses your {gemini.KeyHint()} (encrypted for your Windows user).";
            }
            catch (Exception ex)
            {
                Status = $"Could not save the key: {ex.Message}";
            }
            _ = RefreshInfoAsync();
            return Task.CompletedTask;
        });

        RemoveGeminiKeyCommand = new AsyncCommand((_, _) =>
        {
            gemini.SetApiKey(null);
            session?.ClearThread("gemini");
            Status = "Gemini API key removed.";
            _ = RefreshInfoAsync();
            return Task.CompletedTask;
        });

        SetModelCommand = new AsyncCommand((_, _) =>
        {
            if (busy) return Task.CompletedTask;
            var provider = SelectedProvider;
            provider.SetModel(modelInput);
            session?.ClearThread(provider.ProviderId); // The next message starts a new session with this model.
            ModelInput = provider.CurrentModel;
            Status = $"{provider.DisplayName} model: {provider.CurrentModel}. The next message uses it.";
            _ = RefreshInfoAsync();
            _ = RefreshModelsAsync();
            return Task.CompletedTask;
        });

        AttachCommand = new AsyncCommand(async (_, clientContext, cancellationToken) =>
        {
            if (busy) return;
            var editor = await EditorContext.CaptureAsync(clientContext, cancellationToken);
            var current = EnsureSession(editor);
            if (current is null)
            {
                Status = "Open any file from your solution first; the task list belongs to that solution.";
                return;
            }
            var path = attachPath.Trim().Trim('"');
            if (path.Length == 0)
            {
                Status = "Paste the file path (in Explorer: Shift + right-click → Copy as path), then press Attach.";
                return;
            }
            if (!Path.IsPathRooted(path)) path = Path.Combine(current.Workspace.Root, path);
            LoadPlan(current, path);
        });

        AttachOpenFileCommand = new AsyncCommand(async (_, clientContext, cancellationToken) =>
        {
            if (busy) return;
            var editor = await EditorContext.CaptureAsync(clientContext, cancellationToken);
            var current = EnsureSession(editor);
            if (current is null || editor is null)
            {
                Status = "Open the plan file (.md, .txt, .csv) in the editor first.";
                return;
            }
            LoadPlan(current, editor.FilePath);
        });

        RunNextTaskCommand = new AsyncCommand(async (_, cancellationToken) => await RunTasksAsync(null, cancellationToken));

        ExportTasksCommand = new AsyncCommand((_, _) =>
        {
            if (plan is null) return Task.CompletedTask;
            try
            {
                var file = plan.ExportResults();
                Status = $"Results saved: {file}";
            }
            catch (Exception ex)
            {
                Status = $"Could not save the results: {ex.Message}";
            }
            return Task.CompletedTask;
        });

        ClearTasksCommand = new AsyncCommand((_, _) =>
        {
            if (busy || session is null) return Task.CompletedTask;
            plan = null;
            try { if (File.Exists(session.TasksPath)) File.Delete(session.TasksPath); } catch (IOException) { }
            RefreshTasks();
            Status = "Task list removed from this chat.";
            return Task.CompletedTask;
        });

        _ = CheckConnectionAsync();
        _ = RefreshInfoAsync();
        _ = RefreshModelsAsync();
    }

    private IChatProvider SelectedProvider => isGemini ? gemini : chatGpt;

    // One chat turn: saves the user message, runs the engine, streams into the transcript, saves the answer.
    // Returns Ok=false on error; Stopped=true when the user pressed Stop.
    private async Task<(bool Ok, bool Stopped, string Answer)> RunTurnAsync(ConversationSession current, IChatProvider provider,
        string displayText, string engineText, EditorContext? editor, string? activeFilePath, CancellationToken cancellationToken)
    {
        var root = current.Workspace.Root;
        var handoff = current.BuildHandoff(provider.ProviderId);
        var activeFile = activeFilePath is null ? null : Path.GetRelativePath(root, activeFilePath);
        current.Add("user", provider.ProviderId, displayText, activeFile);

        using var view = new TurnView(this, current.RenderTranscript(), provider.DisplayName);
        runningProvider = provider;
        turnCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsRunning = true;
        Status = "Working... (Stop cancels this reply)";
        try
        {
            var request = new ChatTurnRequest(engineText, root, editor, handoff);
            var answer = await provider.SendAsync(current, request, view, turnCancel.Token);
            current.Add("assistant", provider.ProviderId, view.WithActivity(answer));
            current.MarkSeen(provider.ProviderId);
            Transcript = current.RenderTranscript();
            Status = $"Connected to {provider.DisplayName}";
            var stopped = answer.EndsWith("(Stopped.)", StringComparison.Ordinal);
            return (!stopped, stopped, answer);
        }
        catch (OperationCanceledException)
        {
            current.Add("assistant", provider.ProviderId, view.WithActivity("(Stopped.)"));
            Transcript = current.RenderTranscript();
            Status = "Stopped.";
            return (false, true, "(Stopped.)");
        }
        catch (Exception ex)
        {
            current.Add("assistant", provider.ProviderId, view.WithActivity($"[OwnBridge error] {ex.Message}"));
            Transcript = current.RenderTranscript();
            Status = "Could not complete the message. Your prompt is still in this chat.";
            return (false, false, ex.Message);
        }
        finally
        {
            ClearApproval(approved: false);
            runningProvider = null;
            turnCancel?.Dispose();
            turnCancel = null;
            IsRunning = false;
            RefreshHistory();
            _ = RefreshInfoAsync();
            _ = RefreshModelsAsync();
        }
    }

    // Runs one task (or the next pending ones when Auto-continue is on), each as its own chat turn.
    private async Task RunTasksAsync(PlanTask? specific, CancellationToken cancellationToken)
    {
        if (busy) return;
        if (plan is null || session is null)
        {
            Status = "Attach a plan or issue list first.";
            return;
        }
        Busy = true;
        var current = session;
        var provider = SelectedProvider;
        try
        {
            while (true)
            {
                var task = specific ?? plan.NextPending;
                specific = null;
                if (task is null)
                {
                    Status = $"All tasks are handled ({plan.DoneCount} of {plan.Tasks.Count} done). Use Export results to save a report.";
                    break;
                }

                task.State = TaskState.Running;
                SavePlan();
                taskChangedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var display = $"[Task {task.Number}/{plan.Tasks.Count}] {task.Title}";
                var result = await RunTurnAsync(current, provider, display, plan.BuildPrompt(task), null, null, cancellationToken);

                task.Summary = result.Answer.Length > 500 ? result.Answer[..500] + "..." : result.Answer;
                task.State = result.Ok ? TaskState.Done : result.Stopped ? TaskState.Pending : TaskState.Failed;
                if (result.Ok && commitEachTask && taskChangedPaths.Count > 0 && Git.IsRepository(current.Workspace.Root))
                {
                    try
                    {
                        task.Commit = await Git.CommitAsync(current.Workspace.Root, taskChangedPaths.ToList(),
                            $"OwnBridge task {task.Number}: {task.Title}", cancellationToken);
                        Status = $"Task {task.Number} done and committed ({task.Commit}).";
                    }
                    catch (Exception ex)
                    {
                        task.Summary += $" [commit failed: {ex.Message}]";
                        Status = $"Task {task.Number} done, but the commit failed: {ex.Message}";
                    }
                }
                taskChangedPaths = null;
                SavePlan();
                if (!result.Ok || !autoContinue) break;
            }
        }
        finally
        {
            taskChangedPaths = null;
            Busy = false;
        }
    }

    private void LoadPlan(ConversationSession current, string path)
    {
        try
        {
            var attachment = Attachment.Read(path);
            var loaded = TaskPlan.FromAttachment(attachment);
            if (loaded.Tasks.Count == 0)
            {
                Status = $"No tasks found in {attachment.FileName}.";
                return;
            }
            plan = loaded;
            SavePlan();
            AttachPath = string.Empty;
            Status = $"{loaded.Tasks.Count} tasks from {attachment.FileName}. Press Run next (or Run on a task).";
        }
        catch (Exception ex)
        {
            Status = $"Could not read the file: {ex.Message}";
        }
    }

    private void SavePlan()
    {
        if (plan is not null && session is not null)
        {
            try { plan.Save(session.TasksPath); } catch (IOException) { }
        }
        RefreshTasks();
    }

    private void RefreshTasks()
    {
        TaskRows.Clear();
        HasTasks = plan is not null;
        if (plan is null)
        {
            TaskHeader = string.Empty;
            return;
        }
        TaskHeader = $"Tasks from {plan.SourceName}: {plan.DoneCount} of {plan.Tasks.Count} done";
        foreach (var task in plan.Tasks.Take(300))
        {
            var item = task;
            var key = item.Key.Length > 0 ? $"[{item.Key}] " : string.Empty;
            var commit = item.Commit.Length > 0 ? $"  · commit {item.Commit}" : string.Empty;
            TaskRows.Add(new TaskRow($"{item.Icon} {item.Number}. {key}{item.Title}{commit}",
                new AsyncCommand(async (_, cancellationToken) =>
                {
                    if (item.State is TaskState.Done or TaskState.Skipped or TaskState.Failed) item.State = TaskState.Pending;
                    await RunTasksAsync(item, cancellationToken);
                }),
                new AsyncCommand((_, _) =>
                {
                    if (busy) return Task.CompletedTask;
                    item.State = item.State == TaskState.Skipped ? TaskState.Pending : TaskState.Skipped;
                    SavePlan();
                    return Task.CompletedTask;
                })));
        }
    }

    private void SwitchTo(ConversationSession next)
    {
        session?.Dispose();
        session = next;
        plan = TaskPlan.Load(next.TasksPath);
        RefreshTasks();
        RefreshHistory();
        _ = RefreshInfoAsync();
    }

    // Finds the workspace for the active file and makes sure the open conversation belongs to it.
    private ConversationSession? EnsureSession(EditorContext? editor)
    {
        string? root = null;
        if (editor is not null)
        {
            try { root = EditorContext.FindWorkspaceRoot(editor.FilePath); }
            catch { root = null; }
        }

        if (root is not null)
        {
            var workspace = new Workspace(root);
            if (session is null || session.Workspace.Key != workspace.Key)
            {
                SwitchTo(ConversationSession.OpenLatestOrNew(workspace));
                Transcript = session!.RenderTranscript();
            }
        }

        if (session is not null) WorkspaceText = $"Workspace: {session.Workspace.DisplayName}  ({session.Workspace.Root})";
        return session;
    }

    private void RefreshHistory()
    {
        History.Clear();
        if (session is null) return;
        var workspace = session.Workspace;
        foreach (var summary in ConversationSummary.List(workspace).Take(30))
        {
            var id = summary.Id;
            var current = id == session.Id ? "  ← current" : "";
            History.Add(new ConversationItem($"{summary.Updated:dd MMM HH:mm}  {summary.Title}{current}",
                new AsyncCommand((_, _) =>
                {
                    OpenConversation(workspace, id);
                    return Task.CompletedTask;
                })));
        }
    }

    private void OpenConversation(Workspace workspace, string id)
    {
        if (busy) return;
        if (session?.Id == id)
        {
            ShowHistory = false;
            return;
        }
        var opened = ConversationSession.TryOpen(workspace, id);
        if (opened is null)
        {
            Status = "That chat is open in another Visual Studio window. Close it there first.";
            return;
        }
        SwitchTo(opened);
        Transcript = opened.RenderTranscript();
        ShowHistory = false;
    }

    private async Task RefreshInfoAsync()
    {
        var provider = SelectedProvider;
        try
        {
            var info = await provider.GetInfoAsync(session, CancellationToken.None);
            if (!ReferenceEquals(provider, SelectedProvider)) return;
            AccountText = info.Account;
            UsageText = info.Usage;
        }
        catch (Exception ex)
        {
            AccountText = $"{provider.DisplayName}: {ex.Message}";
            UsageText = string.Empty;
        }
    }

    private async Task RefreshModelsAsync()
    {
        var provider = SelectedProvider;
        try
        {
            var models = await provider.GetModelsAsync(CancellationToken.None);
            if (!ReferenceEquals(provider, SelectedProvider)) return;
            ModelOptions.Clear();
            foreach (var model in models) ModelOptions.Add(model);
            if (!busy) ModelInput = provider.CurrentModel;
        }
        catch
        {
            // The list is a convenience; typing a model name still works.
        }
    }

    // Called by the provider while a turn waits; shows the card and waits for a button.
    private async Task<bool> WaitForApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        await approvalGate.WaitAsync(cancellationToken);
        try
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingApproval = completion;
            pendingDiff = request.Diff;
            ApprovalTitle = request.DeletesFiles ? request.Title + " — includes DELETE" : request.Title;
            ApprovalDetail = request.Diff is null ? request.Detail : $"{request.Detail}\n\n{Shorten(request.Diff, 4000)}";
            HasDiff = request.Diff is not null;
            ApprovalDeletes = request.DeletesFiles;
            HasApproval = true;
            Status = "Waiting for your approval...";
            using var registration = cancellationToken.Register(() => completion.TrySetResult(false));
            var approved = await completion.Task;
            if (approved && request.Changes is { } changes && taskChangedPaths is { } paths)
                foreach (var change in changes) paths.Add(change.Path);
            Status = approved ? "Approved. Working..." : "Declined. Working...";
            return approved;
        }
        finally
        {
            approvalGate.Release();
        }
    }

    private void ClearApproval(bool approved)
    {
        var completion = pendingApproval;
        pendingApproval = null;
        pendingDiff = null;
        HasApproval = false;
        HasDiff = false;
        ApprovalDeletes = false;
        ApprovalTitle = string.Empty;
        ApprovalDetail = string.Empty;
        completion?.TrySetResult(approved);
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..max] + "\n[... use View diff to see everything]";

    private async Task CheckConnectionAsync()
    {
        var provider = SelectedProvider;
        try
        {
            var text = await provider.GetStatusAsync(CancellationToken.None);
            if (!busy && ReferenceEquals(provider, SelectedProvider)) Status = text;
        }
        catch (Exception ex)
        {
            if (!busy) Status = $"{provider.DisplayName} unavailable: {ex.Message}";
        }
    }

    // Switching changes the engine for the next message; a running reply keeps its engine.
    private void OnProviderChanged()
    {
        var name = SelectedProvider.DisplayName;
        ConnectLabel = $"Connect {name}";
        HeaderText = $"Phase 6 · {name}";
        if (!busy) _ = CheckConnectionAsync();
        _ = RefreshInfoAsync();
        _ = RefreshModelsAsync();
    }

    // Renders one running turn and warns when the engine has been silent for a while.
    private sealed class TurnView : IChatTurnObserver, IDisposable
    {
        private readonly ChatPanelData owner;
        private readonly string prefix;
        private readonly string speaker;
        private readonly StringBuilder activity = new();
        private readonly Timer silenceTimer;
        private string partial = string.Empty;
        private DateTime lastEvent = DateTime.UtcNow;
        private volatile bool waitingForUser;

        public TurnView(ChatPanelData owner, string prefix, string speaker)
        {
            this.owner = owner;
            this.prefix = prefix;
            this.speaker = speaker;
            silenceTimer = new Timer(_ => CheckSilence(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            Render();
        }

        private void CheckSilence()
        {
            if (waitingForUser) return;
            var seconds = (int)(DateTime.UtcNow - lastEvent).TotalSeconds;
            if (seconds >= 20)
                owner.Status = $"Still waiting for {speaker} ({seconds}s). The AI service may be busy or retrying; Stop cancels.";
        }

        public void OnPartialAnswer(string text)
        {
            lastEvent = DateTime.UtcNow;
            partial = text;
            Render();
        }

        public void OnActivity(string line)
        {
            lastEvent = DateTime.UtcNow;
            lock (activity) activity.Append("\n  • ").Append(line);
            Render();
        }

        public async Task<bool> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            lock (activity) activity.Append("\n  • Approval requested: ").Append(request.Title);
            Render();
            waitingForUser = true;
            try
            {
                return await owner.WaitForApprovalAsync(request, cancellationToken);
            }
            finally
            {
                waitingForUser = false;
                lastEvent = DateTime.UtcNow;
            }
        }

        public string WithActivity(string answer)
        {
            lock (activity)
                return activity.Length == 0 ? answer : $"{activity.ToString().TrimStart('\n')}\n\n{answer}";
        }

        private void Render()
        {
            string steps;
            lock (activity) steps = activity.ToString();
            owner.Transcript = $"{prefix}\n\n{speaker}:{steps}\n{partial}";
        }

        public void Dispose() => silenceTimer.Dispose();
    }

    [DataMember]
    public string Prompt { get => prompt; set => SetProperty(ref prompt, value); }

    [DataMember]
    public string Transcript { get => transcript; set => SetProperty(ref transcript, value); }

    [DataMember]
    public string Status { get => status; set => SetProperty(ref status, value); }

    [DataMember]
    public string AccountText { get => accountText; private set => SetProperty(ref accountText, value); }

    [DataMember]
    public string UsageText { get => usageText; private set => SetProperty(ref usageText, value); }

    [DataMember]
    public bool ShowHistory { get => showHistory; private set => SetProperty(ref showHistory, value); }

    [DataMember]
    public string GeminiKeyInput { get => geminiKeyInput; set => SetProperty(ref geminiKeyInput, value); }

    [DataMember]
    public string ModelInput { get => modelInput; set => SetProperty(ref modelInput, value); }

    [DataMember]
    public ObservableList<string> ModelOptions { get; } = new();

    [DataMember]
    public ObservableList<ConversationItem> History { get; } = new();

    [DataMember]
    public ObservableList<TaskRow> TaskRows { get; } = new();

    [DataMember]
    public string AttachPath { get => attachPath; set => SetProperty(ref attachPath, value); }

    [DataMember]
    public bool HasTasks { get => hasTasks; private set => SetProperty(ref hasTasks, value); }

    [DataMember]
    public string TaskHeader { get => taskHeader; private set => SetProperty(ref taskHeader, value); }

    [DataMember]
    public bool AutoContinue { get => autoContinue; set => SetProperty(ref autoContinue, value); }

    [DataMember]
    public bool CommitEachTask { get => commitEachTask; set => SetProperty(ref commitEachTask, value); }

    [DataMember]
    public bool IsChatGpt
    {
        get => isChatGpt;
        set
        {
            if (value == isChatGpt) return;
            SetProperty(ref isChatGpt, value);
            if (value) { IsGemini = false; OnProviderChanged(); }
        }
    }

    [DataMember]
    public bool IsGemini
    {
        get => isGemini;
        set
        {
            if (value == isGemini) return;
            SetProperty(ref isGemini, value);
            if (value) { IsChatGpt = false; OnProviderChanged(); }
        }
    }

    [DataMember]
    public string ConnectLabel { get => connectLabel; private set => SetProperty(ref connectLabel, value); }

    [DataMember]
    public string HeaderText { get => headerText; private set => SetProperty(ref headerText, value); }

    [DataMember]
    public string WorkspaceText { get => workspaceText; set => SetProperty(ref workspaceText, value); }

    [DataMember]
    public bool IncludeEditorContext { get => includeEditorContext; set => SetProperty(ref includeEditorContext, value); }

    [DataMember]
    public bool Busy { get => busy; private set => SetProperty(ref busy, value); }

    [DataMember]
    public bool IsRunning { get => isRunning; private set => SetProperty(ref isRunning, value); }

    [DataMember]
    public bool HasApproval { get => hasApproval; private set => SetProperty(ref hasApproval, value); }

    [DataMember]
    public bool HasDiff { get => hasDiff; private set => SetProperty(ref hasDiff, value); }

    [DataMember]
    public bool ApprovalDeletes { get => approvalDeletes; private set => SetProperty(ref approvalDeletes, value); }

    [DataMember]
    public string ApprovalTitle { get => approvalTitle; private set => SetProperty(ref approvalTitle, value); }

    [DataMember]
    public string ApprovalDetail { get => approvalDetail; private set => SetProperty(ref approvalDetail, value); }

    [DataMember] public AsyncCommand ConnectCommand { get; }
    [DataMember] public AsyncCommand SendCommand { get; }
    [DataMember] public AsyncCommand StopCommand { get; }
    [DataMember] public AsyncCommand ApproveCommand { get; }
    [DataMember] public AsyncCommand DeclineCommand { get; }
    [DataMember] public AsyncCommand ViewDiffCommand { get; }
    [DataMember] public AsyncCommand NewChatCommand { get; }
    [DataMember] public AsyncCommand ToggleHistoryCommand { get; }
    [DataMember] public AsyncCommand SaveGeminiKeyCommand { get; }
    [DataMember] public AsyncCommand RemoveGeminiKeyCommand { get; }
    [DataMember] public AsyncCommand SetModelCommand { get; }
    [DataMember] public AsyncCommand AttachCommand { get; }
    [DataMember] public AsyncCommand AttachOpenFileCommand { get; }
    [DataMember] public AsyncCommand RunNextTaskCommand { get; }
    [DataMember] public AsyncCommand ExportTasksCommand { get; }
    [DataMember] public AsyncCommand ClearTasksCommand { get; }

    public void Dispose()
    {
        ClearApproval(approved: false);
        session?.Dispose();
        chatGpt.Dispose();
        gemini.Dispose();
    }
}

// One row in the History list; the row owns its Open command.
[DataContract]
internal sealed class ConversationItem
{
    public ConversationItem(string title, AsyncCommand openCommand)
    {
        Title = title;
        OpenCommand = openCommand;
    }

    [DataMember]
    public string Title { get; private set; }

    [DataMember]
    public AsyncCommand OpenCommand { get; private set; }
}

// One row in the task list, with its own Run and Skip commands.
[DataContract]
internal sealed class TaskRow
{
    public TaskRow(string line, AsyncCommand runCommand, AsyncCommand skipCommand)
    {
        Line = line;
        RunCommand = runCommand;
        SkipCommand = skipCommand;
    }

    [DataMember]
    public string Line { get; private set; }

    [DataMember]
    public AsyncCommand RunCommand { get; private set; }

    [DataMember]
    public AsyncCommand SkipCommand { get; private set; }
}
