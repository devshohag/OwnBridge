namespace OwnBridge;

// Each provider manages its own sign-in and session; the UI owns the shared conversation.
internal interface IChatProvider : IDisposable
{
    string ProviderId { get; }
    Task<string> GetStatusAsync(CancellationToken cancellationToken);
    Task<string> ConnectAsync(Action<string> onStatus, CancellationToken cancellationToken);
    Task<string> SendAsync(ConversationSession session, ChatTurnRequest request,
        IChatTurnObserver observer, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

// What the user asked, plus the Visual Studio context that goes with it.
internal sealed record ChatTurnRequest(string UserText, string WorkspaceRoot, EditorContext? Editor);

// The UI side of a running turn: streaming text, activity lines and approval prompts.
internal interface IChatTurnObserver
{
    void OnPartialAnswer(string text);
    void OnActivity(string line);
    Task<bool> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

// Kind is "file" or "command". Diff is set for file changes when the engine provides one.
internal sealed record ApprovalRequest(string Kind, string Title, string Detail, string? Diff);
