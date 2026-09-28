namespace OwnBridge;

// Each provider manages its own sign-in and session; the UI owns the shared conversation.
internal interface IChatProvider : IDisposable
{
    string ProviderId { get; }
    Task<string> GetStatusAsync(CancellationToken cancellationToken);
    Task<string> ConnectAsync(CancellationToken cancellationToken);
    Task<string> SendAsync(ConversationSession session, string text,
        Action<string> onPartialAnswer, CancellationToken cancellationToken);
}
