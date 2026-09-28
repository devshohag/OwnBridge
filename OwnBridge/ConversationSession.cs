using System.Text;

namespace OwnBridge;

// OwnBridge's conversation is independent of each provider's internal thread.
internal sealed class ConversationSession
{
    private readonly List<ConversationMessage> messages = new();
    private readonly Dictionary<string, string> providerThreads = new(StringComparer.Ordinal);

    public IReadOnlyList<ConversationMessage> Messages => messages;

    public string? ThreadFor(string providerId) =>
        providerThreads.TryGetValue(providerId, out var threadId) ? threadId : null;

    public void SetThread(string providerId, string threadId) => providerThreads[providerId] = threadId;

    public void Add(string role, string providerId, string text) =>
        messages.Add(new ConversationMessage(role, providerId, text, DateTimeOffset.UtcNow));

    public string RenderTranscript()
    {
        var text = new StringBuilder("OwnBridge — Phase 2");
        foreach (var message in messages)
        {
            var speaker = message.Role == "user" ? "You" :
                message.ProviderId == "chatgpt" ? "ChatGPT" : message.ProviderId;
            text.Append("\n\n").Append(speaker).Append(": ").Append(message.Text);
        }
        return text.ToString();
    }
}

internal sealed record ConversationMessage(string Role, string ProviderId, string Text, DateTimeOffset CreatedUtc);
