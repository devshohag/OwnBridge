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

    // Model choice. "default" means the engine decides. Changing it starts a new engine session.
    string CurrentModel { get; }
    Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken);
    void SetModel(string model);

    // Account name and usage for the header; never includes secrets.
    Task<ProviderInfo> GetInfoAsync(ConversationSession? session, CancellationToken cancellationToken);
}

internal sealed record ProviderInfo(string Account, string Usage);

// What the user asked, the Visual Studio context, and (after a switch) what happened earlier.
// AttachedText holds files the user attached to this message; Images are image files the engine sends as pictures.
internal sealed record ChatTurnRequest(string UserText, string WorkspaceRoot, EditorContext? Editor, string? Handoff,
    string? AttachedText = null, IReadOnlyList<string>? Images = null)
{
    public IReadOnlyList<string> ImagePaths => Images ?? Array.Empty<string>();

    public string BuildPrompt()
    {
        var parts = new List<string>();
        if (Handoff is not null) parts.Add(Handoff);
        if (Editor is not null) parts.Add(Editor.ToPromptBlock(WorkspaceRoot));
        if (AttachedText is not null) parts.Add(AttachedText);
        parts.Add(parts.Count == 0 ? UserText : $"User request:\n{UserText}");
        return string.Join("\n\n", parts);
    }
}

// The UI side of a running turn: streaming text, activity lines and approval prompts.
internal interface IChatTurnObserver
{
    void OnPartialAnswer(string text);
    void OnActivity(string line);
    // Short progress notes the AI writes while working ("I'll search for ..."); shown so approvals make sense.
    void OnCommentary(string text);
    // The engine is alive and working (any event for this turn). status, when given, is shown on the status line.
    void OnProgress(string? status);
    // The model's reasoning summary while it thinks, shown in the reply bubble until the answer starts.
    void OnThinking(string text);
    Task<bool> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

// Kind is "file", "command" or "tool". Diff is set for file changes when the engine provides one.
// Changes lists each file with EDIT, CREATE or DELETE, so the card can label (and warn about) them.
internal sealed record ApprovalRequest(string Kind, string Title, string Detail, string? Diff,
    IReadOnlyList<FileChange>? Changes = null, string? Why = null, string? Command = null)
{
    public bool DeletesFiles => Changes?.Any(c => c.Action == "DELETE") == true;
}

internal sealed record FileChange(string Path, string Action)
{
    public static string ActionFor(string engineKind) => engineKind.ToLowerInvariant() switch
    {
        "add" or "create" or "new" => "CREATE",
        "delete" or "remove" => "DELETE",
        _ => "EDIT",
    };
}
