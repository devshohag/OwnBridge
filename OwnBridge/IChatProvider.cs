namespace OwnBridge;

// Each provider manages its own sign-in and engine; the UI owns the shared conversation.
internal interface IChatProvider : IDisposable
{
    string ProviderId { get; }
    string DisplayName { get; }

    // Progress text while an engine is installed or started.
    Action<string>? Progress { get; set; }

    Task<string> GetStatusAsync(CancellationToken cancellationToken);
    Task<string> ConnectAsync(Action<string> onStatus, CancellationToken cancellationToken);
    Task<string> SendAsync(ConversationSession session, ChatTurnRequest request,
        IChatTurnObserver observer, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);

    // Account name and usage for the header; never includes secrets.
    Task<ProviderInfo> GetInfoAsync(ConversationSession? session, CancellationToken cancellationToken);
}

internal sealed record ProviderInfo(string Account, string Usage);

// What the user asked, the Visual Studio context, and (after a switch) what happened earlier.
internal sealed record ChatTurnRequest(string UserText, string WorkspaceRoot, EditorContext? Editor, string? Handoff)
{
    public string BuildPrompt()
    {
        var parts = new List<string>();
        if (Handoff is not null) parts.Add(Handoff);
        if (Editor is not null) parts.Add(Editor.ToPromptBlock(WorkspaceRoot));
        parts.Add(parts.Count == 0 ? UserText : $"User request:\n{UserText}");
        return string.Join("\n\n", parts);
    }
}

// The UI side of a running turn: streaming text, activity lines and approval prompts.
internal interface IChatTurnObserver
{
    void OnPartialAnswer(string text);
    void OnActivity(string line);
    Task<bool> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

// Kind is "file", "command" or "tool". Diff is set for file changes when the engine provides one.
internal sealed record ApprovalRequest(string Kind, string Title, string Detail, string? Diff);
