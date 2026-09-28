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
    private readonly IChatProvider provider = new CodexChatProvider();
    private readonly ConversationSession session = new();
    private readonly SemaphoreSlim approvalGate = new(1, 1);
    private TaskCompletionSource<bool>? pendingApproval;
    private string? pendingDiff;
    private string? lastWorkspaceRoot;

    private string prompt = string.Empty;
    private string transcript = "OwnBridge — Phase 3\n\nOpen a file from your solution, then ask ChatGPT about it or ask it to change code.";
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

        ConnectCommand = new AsyncCommand(async (_, cancellationToken) =>
        {
            if (busy) return;
            Busy = true;
            try
            {
                Status = "Checking ChatGPT sign-in...";
                Status = await provider.ConnectAsync(update => Status = update, cancellationToken);
            }
            catch (Exception ex)
            {
                Status = $"Connection failed: {ex.Message}";
            }
            finally
            {
                Transcript = session.RenderTranscript() + $"\n\nOwnBridge: {Status}";
                Busy = false;
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
                var root = ResolveWorkspaceRoot(editor);
                if (root is null)
                {
                    Status = "Open any file from your solution in the editor, then press Send again.";
                    return;
                }

                session.Add("user", provider.ProviderId, message);
                Prompt = string.Empty;
                var view = new TurnView(this, session.RenderTranscript());
                IsRunning = true;
                Status = "Working... (Stop cancels this reply)";

                try
                {
                    var request = new ChatTurnRequest(message, root, includeEditorContext ? editor : null);
                    var answer = await provider.SendAsync(session, request, view, cancellationToken);
                    session.Add("assistant", provider.ProviderId, view.WithActivity(answer));
                    Transcript = session.RenderTranscript();
                    Status = "Connected to ChatGPT";
                }
                catch (Exception ex)
                {
                    session.Add("assistant", provider.ProviderId, view.WithActivity($"[OwnBridge error] {ex.Message}"));
                    Transcript = session.RenderTranscript();
                    Status = "Could not complete the message. Your prompt is still in this chat.";
                }
            }
            finally
            {
                ClearApproval(approved: false);
                IsRunning = false;
                Busy = false;
            }
        });

        StopCommand = new AsyncCommand(async (_, cancellationToken) =>
        {
            if (!isRunning) return;
            ClearApproval(approved: false);
            Status = "Stopping...";
            try { await provider.StopAsync(cancellationToken); }
            catch (Exception ex) { Status = $"Could not stop: {ex.Message}"; }
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

        _ = CheckConnectionAsync();
    }

    private string? ResolveWorkspaceRoot(EditorContext? editor)
    {
        if (editor is not null)
        {
            try
            {
                var root = EditorContext.FindWorkspaceRoot(editor.FilePath);
                if (root is not null) lastWorkspaceRoot = root;
            }
            catch
            {
                // Keep the previous root if the folder cannot be read.
            }
        }
        if (lastWorkspaceRoot is not null) WorkspaceText = $"Workspace: {lastWorkspaceRoot}";
        return lastWorkspaceRoot;
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

    private async Task CheckConnectionAsync()
    {
        try
        {
            Status = await provider.GetStatusAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Status = $"ChatGPT unavailable: {ex.Message}";
        }
    }

    // Renders one running turn: earlier transcript, activity lines, then the streaming answer.
    private sealed class TurnView : IChatTurnObserver
    {
        private readonly ChatPanelData owner;
        private readonly string prefix;
        private readonly StringBuilder activity = new();
        private string partial = string.Empty;

        public TurnView(ChatPanelData owner, string prefix)
        {
            this.owner = owner;
            this.prefix = prefix;
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
            owner.Transcript = $"{prefix}\n\nChatGPT:{steps}\n{partial}";
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
        provider.Dispose();
    }
}
