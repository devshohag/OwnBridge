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
    private IChatProvider? runningProvider;
    private CancellationTokenSource? turnCancel;
    private bool isChatGpt = true;
    private bool isGemini;
    private string connectLabel = "Connect ChatGPT";
    private string headerText = "Phase 5 · ChatGPT";
    private ConversationSession? session;
    private string accountText = "Account: checking...";
    private string usageText = string.Empty;
    private bool showHistory;
    private string geminiKeyInput = string.Empty;
    private string geminiModelInput = Settings.GeminiModel;
    private readonly SemaphoreSlim approvalGate = new(1, 1);
    private TaskCompletionSource<bool>? pendingApproval;
    private string? pendingDiff;

    private string prompt = string.Empty;
    private string transcript = "OwnBridge — Phase 5\n\nChoose ChatGPT or Gemini, open a file from your solution, then ask about it or ask for a code change. Each solution keeps its own chat history.";
    private string status = "Checking connection...";
    private string workspaceText = "Workspace: open a file from your solution";
    private bool includeEditorContext = true;
    private bool busy;
    private bool isRunning;
    private bool hasApproval;
    private bool hasDiff;
    private string approvalTitle = string.Empty;
    private string approvalDetail = string.Empty;

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
                Transcript = (session?.RenderTranscript() ?? "OwnBridge — Phase 5") + $"\n\nOwnBridge: {Status}";
                Busy = false;
                _ = RefreshInfoAsync();
            }
        });

        SendCommand = new AsyncCommand(async (parameter, clientContext, cancellationToken) =>
        {
            if (busy) return;
            var message = (parameter as string)?.Trim();
            if (string.IsNullOrWhiteSpace(message)) return;

            Busy = true;
            var provider = SelectedProvider;
            try
            {
                var editor = await EditorContext.CaptureAsync(clientContext, cancellationToken);
                var current = EnsureSession(editor);
                if (current is null)
                {
                    Status = "Open any file from your solution in the editor, then press Send again.";
                    return;
                }
                var root = current.Workspace.Root;

                // Handoff: whatever happened in this conversation since this AI last answered.
                var handoff = current.BuildHandoff(provider.ProviderId);
                var activeFile = editor is null ? null : Path.GetRelativePath(root, editor.FilePath);
                current.Add("user", provider.ProviderId, message, activeFile);
                Prompt = string.Empty;
                var view = new TurnView(this, current.RenderTranscript(), provider.DisplayName);
                runningProvider = provider;
                turnCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                IsRunning = true;
                Status = "Working... (Stop cancels this reply)";

                try
                {
                    var request = new ChatTurnRequest(message, root, includeEditorContext ? editor : null, handoff);
                    var answer = await provider.SendAsync(current, request, view, turnCancel.Token);
                    current.Add("assistant", provider.ProviderId, view.WithActivity(answer));
                    current.MarkSeen(provider.ProviderId);
                    Transcript = current.RenderTranscript();
                    Status = $"Connected to {provider.DisplayName}";
                }
                catch (OperationCanceledException)
                {
                    current.Add("assistant", provider.ProviderId, view.WithActivity("(Stopped.)"));
                    Transcript = current.RenderTranscript();
                    Status = "Stopped.";
                }
                catch (Exception ex)
                {
                    current.Add("assistant", provider.ProviderId, view.WithActivity($"[OwnBridge error] {ex.Message}"));
                    Transcript = current.RenderTranscript();
                    Status = "Could not complete the message. Your prompt is still in this chat.";
                }
            }
            finally
            {
                ClearApproval(approved: false);
                runningProvider = null;
                turnCancel?.Dispose();
                turnCancel = null;
                IsRunning = false;
                Busy = false;
                RefreshHistory();
                _ = RefreshInfoAsync();
            }
        });

        StopCommand = new AsyncCommand(async (_, cancellationToken) =>
        {
            if (!isRunning) return;
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
            session.Dispose();
            session = ConversationSession.CreateNew(workspace);
            Transcript = session.RenderTranscript() + "\n\nNew chat started.";
            ShowHistory = false;
            RefreshHistory();
            _ = RefreshInfoAsync();
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
                ForgetGeminiSession();
                Status = $"Saved. Gemini now uses your {gemini.KeyHint()} (encrypted for your Windows user).";
            }
            catch (Exception ex)
            {
                Status = $"Could not save the key: {ex.Message}";
            }
            _ = RefreshInfoAsync();
            return Task.CompletedTask;
        });

        SetGeminiModelCommand = new AsyncCommand((_, _) =>
        {
            if (busy) return Task.CompletedTask;
            gemini.SetModel(geminiModelInput);
            GeminiModelInput = gemini.Model;
            ForgetGeminiSession();
            Status = $"Gemini model set to {gemini.Model}.";
            _ = RefreshInfoAsync();
            return Task.CompletedTask;
        });

        RemoveGeminiKeyCommand = new AsyncCommand((_, _) =>
        {
            gemini.SetApiKey(null);
            ForgetGeminiSession();
            Status = "Gemini API key removed.";
            _ = RefreshInfoAsync();
            return Task.CompletedTask;
        });

        _ = CheckConnectionAsync();
        _ = RefreshInfoAsync();
    }

    // The Gemini engine restarted, so its old session id is no longer valid.
    private void ForgetGeminiSession() => session?.ClearThread("gemini");

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
                session?.Dispose();
                session = ConversationSession.OpenLatestOrNew(workspace);
                Transcript = session.RenderTranscript();
                RefreshHistory();
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
        session?.Dispose();
        session = opened;
        Transcript = session.RenderTranscript();
        ShowHistory = false;
        RefreshHistory();
        _ = RefreshInfoAsync();
    }

    private async Task RefreshInfoAsync()
    {
        var provider = (IChatProvider)(isGemini ? gemini : chatGpt);
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

    // Called by the provider while a turn waits; shows the card and waits for a button.
    private async Task<bool> WaitForApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        await approvalGate.WaitAsync(cancellationToken);
        try
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingApproval = completion;
            pendingDiff = request.Diff;
            ApprovalTitle = request.Title;
            ApprovalDetail = request.Diff is null ? request.Detail : $"{request.Detail}\n\n{Shorten(request.Diff, 4000)}";
            HasDiff = request.Diff is not null;
            HasApproval = true;
            Status = "Waiting for your approval...";
            using var registration = cancellationToken.Register(() => completion.TrySetResult(false));
            var approved = await completion.Task;
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
        ApprovalTitle = string.Empty;
        ApprovalDetail = string.Empty;
        completion?.TrySetResult(approved);
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..max] + "\n[... use View diff to see everything]";

    private IChatProvider SelectedProvider => isGemini ? gemini : chatGpt;

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
        HeaderText = $"Phase 5 · {name}";
        if (!busy) _ = CheckConnectionAsync();
        _ = RefreshInfoAsync();
    }

    // Renders one running turn: earlier transcript, activity lines, then the streaming answer.
    private sealed class TurnView : IChatTurnObserver
    {
        private readonly ChatPanelData owner;
        private readonly string prefix;
        private readonly string speaker;
        private readonly StringBuilder activity = new();
        private string partial = string.Empty;

        public TurnView(ChatPanelData owner, string prefix, string speaker)
        {
            this.owner = owner;
            this.prefix = prefix;
            this.speaker = speaker;
            Render();
        }

        public void OnPartialAnswer(string text)
        {
            partial = text;
            Render();
        }

        public void OnActivity(string line)
        {
            lock (activity) activity.Append("\n  • ").Append(line);
            Render();
        }

        public Task<bool> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            lock (activity) activity.Append("\n  • Approval requested: ").Append(request.Title);
            Render();
            return owner.WaitForApprovalAsync(request, cancellationToken);
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
    }

    [DataMember]
    public string Prompt
    {
        get => prompt;
        set => SetProperty(ref prompt, value);
    }

    [DataMember]
    public string Transcript
    {
        get => transcript;
        set => SetProperty(ref transcript, value);
    }

    [DataMember]
    public string Status
    {
        get => status;
        set => SetProperty(ref status, value);
    }

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
    public string ConnectLabel
    {
        get => connectLabel;
        private set => SetProperty(ref connectLabel, value);
    }

    [DataMember]
    public string HeaderText
    {
        get => headerText;
        private set => SetProperty(ref headerText, value);
    }

    [DataMember]
    public string AccountText
    {
        get => accountText;
        private set => SetProperty(ref accountText, value);
    }

    [DataMember]
    public string UsageText
    {
        get => usageText;
        private set => SetProperty(ref usageText, value);
    }

    [DataMember]
    public bool ShowHistory
    {
        get => showHistory;
        private set => SetProperty(ref showHistory, value);
    }

    [DataMember]
    public string GeminiKeyInput
    {
        get => geminiKeyInput;
        set => SetProperty(ref geminiKeyInput, value);
    }

    [DataMember]
    public string GeminiModelInput
    {
        get => geminiModelInput;
        set => SetProperty(ref geminiModelInput, value);
    }

    [DataMember]
    public AsyncCommand SetGeminiModelCommand { get; }

    [DataMember]
    public ObservableList<ConversationItem> History { get; } = new();

    [DataMember]
    public AsyncCommand NewChatCommand { get; }

    [DataMember]
    public AsyncCommand ToggleHistoryCommand { get; }

    [DataMember]
    public AsyncCommand SaveGeminiKeyCommand { get; }

    [DataMember]
    public AsyncCommand RemoveGeminiKeyCommand { get; }

    [DataMember]
    public string WorkspaceText
    {
        get => workspaceText;
        set => SetProperty(ref workspaceText, value);
    }

    [DataMember]
    public bool IncludeEditorContext
    {
        get => includeEditorContext;
        set => SetProperty(ref includeEditorContext, value);
    }

    [DataMember]
    public bool Busy
    {
        get => busy;
        private set => SetProperty(ref busy, value);
    }

    [DataMember]
    public bool IsRunning
    {
        get => isRunning;
        private set => SetProperty(ref isRunning, value);
    }

    [DataMember]
    public bool HasApproval
    {
        get => hasApproval;
        private set => SetProperty(ref hasApproval, value);
    }

    [DataMember]
    public bool HasDiff
    {
        get => hasDiff;
        private set => SetProperty(ref hasDiff, value);
    }

    [DataMember]
    public string ApprovalTitle
    {
        get => approvalTitle;
        private set => SetProperty(ref approvalTitle, value);
    }

    [DataMember]
    public string ApprovalDetail
    {
        get => approvalDetail;
        private set => SetProperty(ref approvalDetail, value);
    }

    [DataMember]
    public AsyncCommand ConnectCommand { get; }

    [DataMember]
    public AsyncCommand SendCommand { get; }

    [DataMember]
    public AsyncCommand StopCommand { get; }

    [DataMember]
    public AsyncCommand ApproveCommand { get; }

    [DataMember]
    public AsyncCommand DeclineCommand { get; }

    [DataMember]
    public AsyncCommand ViewDiffCommand { get; }

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
