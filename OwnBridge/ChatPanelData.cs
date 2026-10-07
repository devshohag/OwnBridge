using System.Runtime.Serialization;
using System.Text;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.UI;

namespace OwnBridge;

[DataContract]
internal sealed class ChatPanelData : NotifyPropertyChangedObject, IDisposable
{

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
    private string headerText = "ChatGPT";
    private string accountText = "Account: checking...";
    private string usageText = string.Empty;
    private bool showHistory;
    private string geminiKeyInput = string.Empty;
    private string modelInput = "default";
    private string prompt = string.Empty;
    private string emptyText = "Ask about your solution, describe a change, attach a file or paste an error screenshot, or open Tasks for a plan / Excel issue list.";
    private string activeView = "chat";
    private bool showAllMessages;
    private string earlierText = string.Empty;
    // Approval mode: "ask" (every step), "reads" (read-only commands run), "edits" (also file edits and builds
    // inside the solution), "auto" (everything except deleting files and risky commands). Saved per user.
    private string approvalMode = Settings.Get("approvalMode") ?? (Settings.Get("autoApproveReads") == "true" ? "reads" : "ask");
    private string badgeText = string.Empty;
    private bool badgeDanger;
    private string approvalHeadline = string.Empty;
    private string approvalWhy = string.Empty;
    private string approvalCommand = string.Empty;
    private string status = "Checking connection...";
    private string workspaceText = "No solution open";
    private bool includeEditorContext = true;
    private bool busy;
    private bool isRunning;
    private bool hasApproval;
    private bool hasDiff;
    private bool approvalDeletes;
    private string attachPath = string.Empty;
    private bool hasTasks;
    private string taskHeader = string.Empty;
    private bool autoContinue;
    private bool commitEachTask = true;
    private readonly List<ChatFile> pendingFiles = new();
    private bool hasPendingFiles;
    private string chatInfoText = string.Empty;
    private bool hasChatInfo;

    public ChatPanelData(VisualStudioExtensibility extensibility)
    {
        this.extensibility = extensibility;
        chatGpt.Progress = update => Status = update;
        gemini.Progress = update => Status = update;
        _ = DetectSolutionAsync();
        SetApprovalMode(approvalMode is "ask" or "reads" or "edits" or "auto" ? approvalMode : "ask");
        SetEffort(effort is "low" or "medium" or "high" ? effort : string.Empty, announce: false);

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
                RenderMessages();
                Busy = false;
                _ = RefreshInfoAsync();
                _ = RefreshModelsAsync();
            }
        });

        SendCommand = new AsyncCommand(async (parameter, clientContext, cancellationToken) =>
        {
            if (busy) return;
            var message = (parameter as string)?.Trim() ?? string.Empty;
            if (message.Length == 0 && pendingFiles.Count == 0) return;
            if (message.Length == 0)
                message = pendingFiles.Any(f => f.IsImage)
                    ? "Look at the attached image. If it shows an error, find the cause in this solution and suggest the fix."
                    : "Look at the attached file(s).";

            Busy = true;
            try
            {
                var editor = await EditorContext.CaptureAsync(clientContext, cancellationToken);
                var current = await ResolveSessionAsync(editor, allowGeneral: true, cancellationToken);
                if (current is null)
                {
                    Status = "Could not open a chat. Open a file from your solution and press Send again.";
                    return;
                }
                if (current.Workspace.IsGeneral)
                    Status = "No solution open — general chat. Open a solution to let the AI read and change your code.";
                Prompt = string.Empty;
                var files = pendingFiles.ToList();
                ClearPendingFiles();
                var display = files.Count == 0 ? message
                    : message + "\n" + string.Join("\n", files.Select(f => (f.IsImage ? "🖼 " : "📎 ") + f.Name));
                await RunTurnAsync(current, SelectedProvider, display, message,
                    includeEditorContext && !current.Workspace.IsGeneral ? editor : null,
                    editor?.FilePath, cancellationToken, files);
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

        NewChatCommand = new AsyncCommand(async (_, clientContext, cancellationToken) =>
        {
            if (busy) return;
            var editor = await EditorContext.CaptureAsync(clientContext, cancellationToken);
            var current = await ResolveSessionAsync(editor, allowGeneral: true, cancellationToken);
            if (current is null) return;
            var workspace = current.Workspace;
            SwitchTo(ConversationSession.CreateNew(workspace));
            RenderMessages();
            Status = workspace.IsGeneral ? "New general chat started (no solution open)." : "New chat started.";
            ActiveView = "chat";
        });

        ToggleHistoryCommand = new AsyncCommand(async (_, clientContext, cancellationToken) =>
        {
            var editor = await EditorContext.CaptureAsync(clientContext, cancellationToken);
            if (await ResolveSessionAsync(editor, allowGeneral: true, cancellationToken) is null) return;
            RefreshHistory();
            ActiveView = activeView == "history" ? "chat" : "history";
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
            var path = attachPath.Trim().Trim('"');
            if (path.Length == 0)
            {
                Status = "Press Browse… to pick the plan or Excel file (or paste its full path).";
                return;
            }
            await AttachPlanAsync(path, clientContext, cancellationToken);
        });

        BrowsePlanCommand = new AsyncCommand(async (_, clientContext, cancellationToken) =>
        {
            if (busy) return;
            var picked = await FileDialog.PickFileAsync("Attach a plan or issue list", FileDialog.PlanFilter, session?.Workspace.Root);
            if (picked is null) return;
            await AttachPlanAsync(picked, clientContext, cancellationToken);
        });

        AttachChatFileCommand = new AsyncCommand(async (_, _) =>
        {
            var initial = session is { Workspace.IsGeneral: false } ? session.Workspace.Root
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            // Ctrl or Shift + click picks several files at once.
            var picked = await FileDialog.PickFilesAsync("Attach files or images to the message (Ctrl + click for several)",
                FileDialog.ChatFilter, initial, multiple: true);
            foreach (var file in picked) AddPendingFile(file);
            if (picked.Count > 1) Status = $"{pendingFiles.Count} files will be sent with your message.";
        });

        PasteImageCommand = new AsyncCommand((_, _) =>
        {
            try
            {
                var file = ChatFiles.SaveClipboardImage();
                if (file is null)
                {
                    Status = "No image on the clipboard. Take a screenshot first (Win + Shift + S), then press Paste image.";
                    return Task.CompletedTask;
                }
                AddPendingFile(file);
            }
            catch (Exception ex)
            {
                Status = $"Could not read the clipboard image: {ex.Message}";
            }
            return Task.CompletedTask;
        });

        AttachOpenFileCommand = new AsyncCommand(async (_, clientContext, cancellationToken) =>
        {
            if (busy) return;
            var editor = await EditorContext.CaptureAsync(clientContext, cancellationToken);
            var current = await ResolveSessionAsync(editor, allowGeneral: false, cancellationToken);
            if (current is null || current.Workspace.IsGeneral || editor is null)
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
            TaskPlan.RemoveForWorkspace(session.Workspace);
            try { if (File.Exists(session.TasksPath)) File.Delete(session.TasksPath); } catch (IOException) { }
            RefreshTasks();
            Status = "Task list removed. Your plan file itself is not changed.";
            return Task.CompletedTask;
        });

        ShowChatCommand = new AsyncCommand((_, _) => { ActiveView = "chat"; return Task.CompletedTask; });
        ShowTasksCommand = new AsyncCommand((_, _) => { ActiveView = activeView == "tasks" ? "chat" : "tasks"; return Task.CompletedTask; });
        ShowHistoryCommand = new AsyncCommand((_, _) => { RefreshHistory(); ActiveView = activeView == "history" ? "chat" : "history"; return Task.CompletedTask; });
        ShowSettingsCommand = new AsyncCommand((_, _) => { ActiveView = activeView == "settings" ? "chat" : "settings"; return Task.CompletedTask; });
        ShowEarlierCommand = new AsyncCommand((_, _) => { ShowAllMessages = !showAllMessages; RenderMessages(); return Task.CompletedTask; });

        _ = CheckConnectionAsync();
        _ = RefreshInfoAsync();
        _ = RefreshModelsAsync();
    }

    // Shows the latest exchange (or everything after "Show earlier"), so new replies stay in view.
    private void RenderMessages()
    {
        Messages.Clear();
        var all = session?.Messages ?? (IReadOnlyList<ConversationMessage>)Array.Empty<ConversationMessage>();
        var start = showAllMessages ? 0 : Math.Max(0, all.Count - 2);
        for (var i = start; i < all.Count; i++) Messages.Add(MessageItem.FromStored(all[i]));
        HasMessages = all.Count > 0;
        if (all.Count > 0)
        {
            var first = all[0].CreatedUtc.ToLocalTime();
            var last = all[^1].CreatedUtc.ToLocalTime();
            var place = session?.Workspace.DisplayName ?? string.Empty;
            ChatInfoText = $"Chat started {first:dd MMM yyyy, HH:mm} · last message {(last.Date == DateTime.Today ? $"today {last:HH:mm}" : $"{last:dd MMM, HH:mm}")} · {place}";
        }
        else ChatInfoText = string.Empty;
        HasChatInfo = all.Count > 0;
        HasEarlier = start > 0 || showAllMessages && all.Count > 2;
        EarlierText = showAllMessages ? "Show only the latest" : $"Show {start} earlier message{(start == 1 ? "" : "s")}";
    }

    private void FillApprovalCard(ApprovalRequest request)
    {
        DiffLines.Clear();
        ApprovalWhy = string.IsNullOrWhiteSpace(request.Why) ? string.Empty : $"Why: {request.Why.Trim()}";
        if (request.Kind == "file")
        {
            var changes = request.Changes ?? Array.Empty<FileChange>();
            var root = session?.Workspace.Root;
            string Rel(string path) => root is not null && Path.IsPathRooted(path) ? Path.GetRelativePath(root, path) : path;
            var actions = changes.Select(c => c.Action).Distinct().ToList();
            BadgeText = actions.Count == 1 ? actions[0] : actions.Count == 0 ? "EDIT" : "CHANGES";
            BadgeDanger = request.DeletesFiles;
            ApprovalHeadline = changes.Count switch
            {
                0 => request.Title,
                1 => Rel(changes[0].Path),
                _ => $"{changes.Count} files: " + string.Join(", ", changes.Take(3).Select(c => $"{c.Action} {Path.GetFileName(c.Path)}")) +
                     (changes.Count > 3 ? ", ..." : string.Empty),
            };
            ApprovalCommand = string.Empty;
            HasCommand = false;
            if (request.Diff is { } diff)
                foreach (var line in diff.Replace("\r\n", "\n").Split('\n').Take(80))
                    DiffLines.Add(new DiffLine(line.Length > 160 ? line[..160] + "…" : line));
        }
        else
        {
            var command = request.Command ?? request.Detail;
            var (kind, label) = CommandSafety.Classify(command);
            BadgeText = kind switch { CommandKind.Read => "READ", CommandKind.Build => "BUILD", CommandKind.Risky => "RISK", _ => "RUN" };
            BadgeDanger = kind == CommandKind.Risky;
            ApprovalHeadline = label;
            ApprovalCommand = command.Length > 1500 ? command[..1500] + "…" : command;
            HasCommand = true;
        }
    }

    private IChatProvider SelectedProvider => isGemini ? gemini : chatGpt;

    // One chat turn: saves the user message, runs the engine, streams into the transcript, saves the answer.
    // Returns Ok=false on error; Stopped=true when the user pressed Stop.
    private async Task<(bool Ok, bool Stopped, string Answer)> RunTurnAsync(ConversationSession current, IChatProvider provider,
        string displayText, string engineText, EditorContext? editor, string? activeFilePath, CancellationToken cancellationToken,
        IReadOnlyList<ChatFile>? files = null)
    {
        var root = current.Workspace.Root;
        var handoff = current.BuildHandoff(provider.ProviderId);
        var activeFile = activeFilePath is null ? null : Path.GetRelativePath(root, activeFilePath);
        current.Add("user", provider.ProviderId, displayText, activeFile);

        ShowAllMessages = false;
        ActiveView = "chat";
        RenderMessages();
        using var view = new TurnView(this, provider.DisplayName);
        runningProvider = provider;
        turnCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsRunning = true;
        Status = "Working... (Stop cancels this reply)";
        try
        {
            var attached = files ?? Array.Empty<ChatFile>();
            var request = new ChatTurnRequest(engineText, root, editor, handoff,
                ChatFiles.BuildTextBlock(attached, root, !current.Workspace.IsGeneral),
                attached.Where(f => f.IsImage).Select(f => f.Path).ToList());
            var answer = await provider.SendAsync(current, request, view, turnCancel.Token);
            current.Add("assistant", provider.ProviderId, view.WithActivity(answer));
            current.MarkSeen(provider.ProviderId);
            RenderMessages();
            Status = $"Connected to {provider.DisplayName}";
            var stopped = answer.EndsWith("(Stopped.)", StringComparison.Ordinal);
            return (!stopped, stopped, answer);
        }
        catch (OperationCanceledException)
        {
            current.Add("assistant", provider.ProviderId, view.WithActivity("(Stopped.)"));
            RenderMessages();
            Status = "Stopped.";
            return (false, true, "(Stopped.)");
        }
        catch (Exception ex)
        {
            current.Add("assistant", provider.ProviderId, view.WithActivity($"[OwnBridge error] {ex.Message}"));
            RenderMessages();
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
                string? writeNote = null;
                if (result.Ok)
                {
                    // Record "done" in the plan/Excel file itself, and commit it together with the task's changes.
                    writeNote = plan.WriteBack(task, done: true);
                    if (writeNote is null && current.Workspace.Contains(plan.SourceFile)) taskChangedPaths.Add(plan.SourceFile);
                    if (writeNote is not null) Status = $"Task {task.Number} done. {writeNote}";
                }
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

    private async Task AttachPlanAsync(string path, IClientContext clientContext, CancellationToken cancellationToken)
    {
        var editor = await EditorContext.CaptureAsync(clientContext, cancellationToken);
        // The plan's own folder decides the workspace; otherwise the open file or solution does.
        var current = (Path.IsPathRooted(path) ? EnsureSessionFor(path) : null)
            ?? await ResolveSessionAsync(editor, allowGeneral: false, cancellationToken);
        if (current is null || current.Workspace.IsGeneral)
        {
            Status = "Open your solution first (or keep the plan inside the project folder), then attach it.";
            return;
        }
        if (!Path.IsPathRooted(path)) path = Path.Combine(current.Workspace.Root, path);
        LoadPlan(current, path);
    }

    private void AddPendingFile(string path)
    {
        var problem = ChatFiles.Validate(path);
        if (problem is not null)
        {
            Status = problem;
            return;
        }
        if (pendingFiles.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        if (pendingFiles.Count >= 10)
        {
            Status = "Up to 10 files per message.";
            return;
        }
        var file = new ChatFile(path, ChatFiles.IsImage(path));
        pendingFiles.Add(file);
        PendingFiles.Add(new AttachmentChip((file.IsImage ? "🖼 " : "📎 ") + file.Name, new AsyncCommand((_, _) =>
        {
            var index = pendingFiles.IndexOf(file);
            if (index >= 0)
            {
                pendingFiles.RemoveAt(index);
                PendingFiles.RemoveAt(index);
            }
            HasPendingFiles = pendingFiles.Count > 0;
            return Task.CompletedTask;
        })));
        HasPendingFiles = true;
        Status = file.IsImage ? $"{file.Name} will be sent with your message."
            : ZipAttachment.IsZip(path) ? $"{file.Name} will be unpacked for the AI when you send (bin/obj/.git are skipped)."
            : $"{file.Name} will be sent with your message (as text).";
    }

    private void ClearPendingFiles()
    {
        pendingFiles.Clear();
        PendingFiles.Clear();
        HasPendingFiles = false;
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
            loaded.MergeProgressFrom(TaskPlan.Load(current.Workspace.PlanStatePath(loaded.SourceFile)));
            plan = loaded;
            SavePlan();
            AttachPath = string.Empty;
            ActiveView = "tasks";
            Status = $"{loaded.Tasks.Count} tasks from {attachment.FileName}, {loaded.DoneCount} already done. Press Run next (or Run on a task).";
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
            try { TaskPlan.SaveForWorkspace(plan, session.Workspace); } catch (IOException) { }
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
                }),
                item.State == TaskState.Done ? "Not done" : "Mark done",
                new AsyncCommand((_, _) =>
                {
                    if (busy || plan is null) return Task.CompletedTask;
                    var done = item.State != TaskState.Done;
                    item.State = done ? TaskState.Done : TaskState.Pending;
                    item.Summary = done ? "Marked done by you." : string.Empty;
                    var note = plan.WriteBack(item, done);
                    SavePlan();
                    Status = note ?? $"Task {item.Number} marked {(done ? "done" : "not done")} in {plan.SourceName}.";
                    return Task.CompletedTask;
                })));
        }
    }

    private void SwitchTo(ConversationSession next)
    {
        session?.Dispose();
        session = next;
        plan = TaskPlan.LoadForWorkspace(next.Workspace);
        if (plan is null && TaskPlan.Load(next.TasksPath) is { } legacy)
        {
            // Older versions kept the list per chat; move it to the solution.
            legacy.MergeProgressFrom(TaskPlan.Load(next.Workspace.PlanStatePath(legacy.SourceFile)));
            plan = legacy;
            try { TaskPlan.SaveForWorkspace(plan, next.Workspace); } catch (IOException) { }
        }
        RefreshTasks();
        RefreshHistory();
        _ = RefreshInfoAsync();
    }

    // Finds the workspace for the active file and makes sure the open conversation belongs to it.
    private ConversationSession? EnsureSession(EditorContext? editor) => EnsureSessionFor(editor?.FilePath);

    // Picks the workspace for this action, in order: the open file's project, the open solution,
    // the chat already open, and (for chat only) the general no-solution chat.
    private async Task<ConversationSession?> ResolveSessionAsync(EditorContext? editor, bool allowGeneral, CancellationToken cancellationToken)
    {
        var fileRoot = RootOf(editor?.FilePath);
        if (fileRoot is not null) return UseRoot(fileRoot);

        var solutionRoot = await SolutionLocator.GetSolutionFolderAsync(extensibility, cancellationToken);
        if (solutionRoot is not null) return UseRoot(solutionRoot);

        if (session is not null && !session.Workspace.IsGeneral) return session;
        if (allowGeneral) return UseRoot(SolutionLocator.GeneralFolder);
        return session;
    }

    // Called when the panel opens so the context line shows the solution before anything is sent.
    private async Task DetectSolutionAsync()
    {
        try
        {
            if (session is not null) return;
            var root = await SolutionLocator.GetSolutionFolderAsync(extensibility, CancellationToken.None);
            if (root is not null && session is null) UseRoot(root);
        }
        catch { }
    }

    // Finds the workspace a file belongs to and makes sure the open conversation is that workspace's.
    // A file outside any project (SDK, NuGet, temp) is ignored and the current workspace stays.
    private ConversationSession? EnsureSessionFor(string? filePath)
    {
        var root = RootOf(filePath);
        return root is not null ? UseRoot(root) : session;
    }

    private static string? RootOf(string? filePath)
    {
        if (filePath is null) return null;
        try { return EditorContext.FindWorkspaceRoot(filePath); }
        catch { return null; }
    }

    private ConversationSession? UseRoot(string root)
    {
        // A folder inside the open workspace (for example a new project's src\ folder) keeps the same chat.
        var keep = session is not null && !session.Workspace.IsGeneral && session.Workspace.Contains(root);
        if (!keep)
        {
            var workspace = new Workspace(root);
            if (session is null || session.Workspace.Key != workspace.Key)
            {
                SwitchTo(ConversationSession.OpenLatestOrNew(workspace));
                RenderMessages();
            }
        }
        if (session is not null) WorkspaceText = session.Workspace.DisplayName;
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
            ActiveView = "chat";
            return;
        }
        var opened = ConversationSession.TryOpen(workspace, id);
        if (opened is null)
        {
            Status = "That chat is open in another Visual Studio window. Close it there first.";
            return;
        }
        SwitchTo(opened);
        RenderMessages();
        ActiveView = "chat";
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
            FillApprovalCard(request);
            HasDiff = request.Diff is not null;
            ApprovalDeletes = request.DeletesFiles;
            HasApproval = true;
            Status = "Waiting for your approval...";
            using var registration = cancellationToken.Register(() => completion.TrySetResult(false));
            var approved = await completion.Task;
            if (approved) RecordApprovedChanges(request);
            Status = approved ? "Approved. Working..." : "Declined. Working...";
            return approved;
        }
        finally
        {
            approvalGate.Release();
        }
    }

    // Files a task changed are committed with that task, whether approved by hand or automatically.
    private void RecordApprovedChanges(ApprovalRequest request)
    {
        if (request.Changes is { } changes && taskChangedPaths is { } paths)
            foreach (var change in changes) paths.Add(change.Path);
    }

    // Returns the activity line when this request may run without asking, or null when the card must ask.
    private string? AutoDecision(ApprovalRequest request)
    {
        var mode = approvalMode;
        if (mode == "ask") return null;
        if (request.Kind == "file")
        {
            if (mode is not ("edits" or "auto") || request.DeletesFiles) return null;
            var changes = request.Changes ?? Array.Empty<FileChange>();
            var root = session?.Workspace;
            if (changes.Count == 0 || root is null || root.IsGeneral) return null;
            // Only files inside the open solution; anything outside it still asks.
            foreach (var change in changes)
            {
                var full = Path.IsPathRooted(change.Path) ? change.Path : Path.Combine(root.Root, change.Path);
                if (!root.Contains(full)) return null;
            }
            return "Auto-approved edit: " + string.Join(", ", changes.Select(c => $"{c.Action} {Path.GetFileName(c.Path)}"));
        }
        var (kind, label) = CommandSafety.Classify(request.Command ?? request.Detail);
        var allowed = kind switch
        {
            CommandKind.Read => true,
            CommandKind.Build => mode is "edits" or "auto",
            CommandKind.Run => mode == "auto",
            _ => false, // Risky: always ask.
        };
        if (request.Kind == "tool") allowed = mode == "auto";
        return allowed ? $"Auto-approved ({label}): {request.Command ?? request.Title}" : null;
    }

    private void SetApprovalMode(string mode)
    {
        approvalMode = mode;
        Settings.Set("approvalMode", mode);
        ModeAsk = mode == "ask";
        ModeReads = mode == "reads";
        ModeEdits = mode == "edits";
        ModeAuto = mode == "auto";
        ApprovalModeText = mode switch
        {
            "reads" => "Approvals: auto-read",
            "edits" => "Approvals: auto-edit",
            "auto" => "Approvals: full auto",
            _ => "Approvals: ask",
        };
    }

    private void ClearApproval(bool approved)
    {
        var completion = pendingApproval;
        pendingApproval = null;
        pendingDiff = null;
        HasApproval = false;
        HasDiff = false;
        ApprovalDeletes = false;
        BadgeText = string.Empty;
        ApprovalHeadline = string.Empty;
        ApprovalWhy = string.Empty;
        ApprovalCommand = string.Empty;
        DiffLines.Clear();
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
        HeaderText = name;
        if (!busy) _ = CheckConnectionAsync();
        _ = RefreshInfoAsync();
        _ = RefreshModelsAsync();
    }

    // One running turn: a live assistant message with activity lines; warns when the engine is silent.
    private sealed class TurnView : IChatTurnObserver, IDisposable
    {
        private readonly ChatPanelData owner;
        private readonly string speaker;
        private readonly MessageItem live;
        private readonly StringBuilder activity = new();
        private readonly Timer silenceTimer;
        private DateTime lastEvent = DateTime.UtcNow;
        private readonly DateTime started = DateTime.UtcNow;
        private volatile bool answerStarted;
        private volatile bool waitingForUser;

        public TurnView(ChatPanelData owner, string speaker)
        {
            this.owner = owner;
            this.speaker = speaker;
            live = new MessageItem(false, speaker) { IsStreaming = true, StreamText = "Thinking…" };
            owner.Messages.Add(live);
            silenceTimer = new Timer(_ => CheckSilence(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        private void CheckSilence()
        {
            if (waitingForUser) return;
            var silent = (int)(DateTime.UtcNow - lastEvent).TotalSeconds;
            var total = (int)(DateTime.UtcNow - started).TotalSeconds;
            // Only real silence is reported; thinking and commands keep the engine "alive".
            if (silent >= 30)
                owner.Status = $"No events from {speaker} for {silent}s (working {total}s). The AI service may be slow right now; Stop cancels.";
        }

        public void OnProgress(string? status)
        {
            lastEvent = DateTime.UtcNow;
            if (status is not null) owner.Status = status;
        }

        // Last part of the reasoning summary, shown until the real answer starts streaming.
        public void OnThinking(string text)
        {
            lastEvent = DateTime.UtcNow;
            if (answerStarted) return;
            var clean = text.ReplaceLineEndings(" ").Trim();
            if (clean.Length > 220) clean = "…" + clean[^220..];
            live.StreamText = "Thinking: " + clean;
        }

        public void OnPartialAnswer(string text)
        {
            answerStarted = true;
            lastEvent = DateTime.UtcNow;
            live.StreamText = text;
        }

        public void OnActivity(string line)
        {
            lastEvent = DateTime.UtcNow;
            lock (activity) activity.Append("\n• ").Append(line);
            live.AddActivity(line);
        }

        public void OnCommentary(string text) => OnActivity("Note: " + text.ReplaceLineEndings(" ").Trim());

        public async Task<bool> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            // The approval mode decides what runs without asking. Deleting files and risky commands always ask.
            if (owner.AutoDecision(request) is { } auto)
            {
                OnActivity(auto);
                owner.RecordApprovedChanges(request);
                return true;
            }

            waitingForUser = true;
            try
            {
                var approved = await owner.WaitForApprovalAsync(request, cancellationToken);
                OnActivity($"{(approved ? "Approved" : "Declined")}: {(request.Kind == "file" ? request.Detail.Split('\n')[0] : Shorten(request.Command ?? request.Title, 120))}");
                return approved;
            }
            finally
            {
                waitingForUser = false;
                lastEvent = DateTime.UtcNow;
            }
        }

        private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";

        public string WithActivity(string answer)
        {
            lock (activity)
                return activity.Length == 0 ? answer : $"{activity.ToString().TrimStart('\n')}\n\n{answer}";
        }

        public void Dispose() => silenceTimer.Dispose();
    }

    [DataMember]
    public string Prompt { get => prompt; set => SetProperty(ref prompt, value); }

    [DataMember]
    public string EmptyText { get => emptyText; private set => SetProperty(ref emptyText, value); }

    [DataMember]
    public ObservableList<MessageItem> Messages { get; } = new();

    [DataMember]
    public ObservableList<DiffLine> DiffLines { get; } = new();

    [DataMember]
    public string ActiveView
    {
        get => activeView;
        set
        {
            SetProperty(ref activeView, value);
            IsChatView = value == "chat";
            IsTasksView = value == "tasks";
            IsHistoryView = value == "history";
            IsSettingsView = value == "settings";
        }
    }

    private bool isChatView = true, isTasksView, isHistoryView, isSettingsView, hasMessages, hasEarlier, hasCommand;

    [DataMember] public bool IsChatView { get => isChatView; private set => SetProperty(ref isChatView, value); }
    [DataMember] public bool IsTasksView { get => isTasksView; private set => SetProperty(ref isTasksView, value); }
    [DataMember] public bool IsHistoryView { get => isHistoryView; private set => SetProperty(ref isHistoryView, value); }
    [DataMember] public bool IsSettingsView { get => isSettingsView; private set => SetProperty(ref isSettingsView, value); }
    [DataMember] public bool HasMessages { get => hasMessages; private set { SetProperty(ref hasMessages, value); NoMessages = !value; } }
    private bool noMessages = true, badgeNormal = true;
    [DataMember] public bool NoMessages { get => noMessages; private set => SetProperty(ref noMessages, value); }
    [DataMember] public bool BadgeNormal { get => badgeNormal; private set => SetProperty(ref badgeNormal, value); }
    [DataMember] public bool HasEarlier { get => hasEarlier; private set => SetProperty(ref hasEarlier, value); }
    [DataMember] public string EarlierText { get => earlierText; private set => SetProperty(ref earlierText, value); }
    [DataMember] public bool ShowAllMessages { get => showAllMessages; private set => SetProperty(ref showAllMessages, value); }
    [DataMember] public bool HasCommand { get => hasCommand; private set => SetProperty(ref hasCommand, value); }

    // ChatGPT reasoning effort: "low" (fast), "medium", "high" (deep), or empty = the engine's own default.
    private string effort = Settings.Get("chatgptEffort") ?? string.Empty;
    private bool speedDefault, speedFast, speedBalanced, speedDeep;
    [DataMember] public bool SpeedDefault { get => speedDefault; set { SetProperty(ref speedDefault, value); if (value) SetEffort(string.Empty); } }
    [DataMember] public bool SpeedFast { get => speedFast; set { SetProperty(ref speedFast, value); if (value) SetEffort("low"); } }
    [DataMember] public bool SpeedBalanced { get => speedBalanced; set { SetProperty(ref speedBalanced, value); if (value) SetEffort("medium"); } }
    [DataMember] public bool SpeedDeep { get => speedDeep; set { SetProperty(ref speedDeep, value); if (value) SetEffort("high"); } }

    private void SetEffort(string value, bool announce = true)
    {
        var changed = effort != value;
        effort = value;
        Settings.Set("chatgptEffort", value);
        SetProperty(ref speedDefault, value.Length == 0, nameof(SpeedDefault));
        SetProperty(ref speedFast, value == "low", nameof(SpeedFast));
        SetProperty(ref speedBalanced, value == "medium", nameof(SpeedBalanced));
        SetProperty(ref speedDeep, value == "high", nameof(SpeedDeep));
        if (!announce || !changed) return;
        Status = value switch
        {
            "low" => "ChatGPT speed: Fast. Replies come sooner with less thinking; good for small questions and edits.",
            "medium" => "ChatGPT speed: Balanced.",
            "high" => "ChatGPT speed: Deep. Slower, more careful; for hard bugs and big tasks.",
            _ => "ChatGPT speed: engine default.",
        };
    }

    private bool modeAsk, modeReads, modeEdits, modeAuto;
    private string approvalModeText = "Approvals: ask";

    // One radio button per mode; setting one to true switches the mode.
    [DataMember] public bool ModeAsk { get => modeAsk; set { SetProperty(ref modeAsk, value); if (value && approvalMode != "ask") SetApprovalMode("ask"); } }
    [DataMember] public bool ModeReads { get => modeReads; set { SetProperty(ref modeReads, value); if (value && approvalMode != "reads") SetApprovalMode("reads"); } }
    [DataMember] public bool ModeEdits { get => modeEdits; set { SetProperty(ref modeEdits, value); if (value && approvalMode != "edits") SetApprovalMode("edits"); } }
    [DataMember] public bool ModeAuto { get => modeAuto; set { SetProperty(ref modeAuto, value); if (value && approvalMode != "auto") SetApprovalMode("auto"); } }
    [DataMember] public string ApprovalModeText { get => approvalModeText; private set => SetProperty(ref approvalModeText, value); }

    [DataMember] public string BadgeText { get => badgeText; private set => SetProperty(ref badgeText, value); }
    [DataMember] public bool BadgeDanger { get => badgeDanger; private set { SetProperty(ref badgeDanger, value); BadgeNormal = !value; } }
    [DataMember] public string ApprovalHeadline { get => approvalHeadline; private set => SetProperty(ref approvalHeadline, value); }
    [DataMember] public string ApprovalWhy { get => approvalWhy; private set => SetProperty(ref approvalWhy, value); }
    [DataMember] public string ApprovalCommand { get => approvalCommand; private set => SetProperty(ref approvalCommand, value); }

    [DataMember] public AsyncCommand ShowChatCommand { get; private set; } = null!;
    [DataMember] public AsyncCommand ShowTasksCommand { get; private set; } = null!;
    [DataMember] public AsyncCommand ShowHistoryCommand { get; private set; } = null!;
    [DataMember] public AsyncCommand ShowSettingsCommand { get; private set; } = null!;
    [DataMember] public AsyncCommand ShowEarlierCommand { get; private set; } = null!;

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
    public ObservableList<AttachmentChip> PendingFiles { get; } = new();

    [DataMember]
    public bool HasPendingFiles { get => hasPendingFiles; private set => SetProperty(ref hasPendingFiles, value); }

    [DataMember]
    public string ChatInfoText { get => chatInfoText; private set => SetProperty(ref chatInfoText, value); }

    [DataMember]
    public bool HasChatInfo { get => hasChatInfo; private set => SetProperty(ref hasChatInfo, value); }

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
    [DataMember] public AsyncCommand BrowsePlanCommand { get; }
    [DataMember] public AsyncCommand AttachChatFileCommand { get; }
    [DataMember] public AsyncCommand PasteImageCommand { get; }
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
