using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OwnBridge;

// One OwnBridge conversation inside one workspace (solution).
// Stored as JSON lines under %LOCALAPPDATA%\OwnBridge\workspaces\<id>\conversations\.
// While a conversation is open in a Visual Studio window it holds an exclusive .lock file,
// so a second window with the same solution can never write into it.
internal sealed class ConversationSession : IDisposable
{
    private const int HandoffChars = 12_000;
    private const int MessageChars = 2_000;

    private readonly List<ConversationMessage> messages = new();
    private readonly Dictionary<string, string> providerThreads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> seenUpTo = new(StringComparer.Ordinal);
    private FileStream? lockStream;

    private ConversationSession(Workspace workspace, string id)
    {
        Workspace = workspace;
        Id = id;
    }

    public Workspace Workspace { get; }

    public string Id { get; }

    public IReadOnlyList<ConversationMessage> Messages => messages;

    public string FilePath => Path.Combine(Workspace.ConversationsFolder, Id + ".jsonl");

    public string TasksPath => Path.Combine(Workspace.ConversationsFolder, Id + ".tasks.json");

    private string LockPath => Path.Combine(Workspace.ConversationsFolder, Id + ".lock");

    public static ConversationSession CreateNew(Workspace workspace)
    {
        var id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var session = new ConversationSession(workspace, id);
        session.TryLock();
        return session;
    }

    // Returns null when the conversation is open in another Visual Studio window.
    public static ConversationSession? TryOpen(Workspace workspace, string id)
    {
        var session = new ConversationSession(workspace, id);
        if (!session.TryLock()) return null;
        try
        {
            foreach (var line in File.ReadLines(session.FilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var message = JsonSerializer.Deserialize<ConversationMessage>(line);
                if (message is not null) session.messages.Add(message);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // A damaged line stops loading; everything before it is kept.
        }
        return session;
    }

    // Opens the newest conversation that no other window is using, or starts a new one.
    public static ConversationSession OpenLatestOrNew(Workspace workspace)
    {
        foreach (var summary in ConversationSummary.List(workspace))
        {
            var session = TryOpen(workspace, summary.Id);
            if (session is not null) return session;
        }
        return CreateNew(workspace);
    }

    private bool TryLock()
    {
        try
        {
            Directory.CreateDirectory(Workspace.ConversationsFolder);
            lockStream = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public string? ThreadFor(string providerId) =>
        providerThreads.TryGetValue(providerId, out var threadId) ? threadId : null;

    public void SetThread(string providerId, string threadId) => providerThreads[providerId] = threadId;

    public void ClearThread(string providerId) => providerThreads.Remove(providerId);

    public void Add(string role, string providerId, string text, string? activeFile = null)
    {
        var message = new ConversationMessage(role, providerId, text, DateTimeOffset.Now, activeFile);
        messages.Add(message);
        try
        {
            Directory.CreateDirectory(Workspace.ConversationsFolder);
            File.AppendAllText(FilePath, JsonSerializer.Serialize(message) + "\n", Encoding.UTF8);
            Workspace.SaveInfo();
        }
        catch (IOException)
        {
            // Saving is best effort; the conversation continues in memory.
        }
    }

    // What happened in this conversation since this provider last answered. Everything
    // is from this conversation only, so no other solution or chat can leak in.
    public string? BuildHandoff(string providerId)
    {
        var from = seenUpTo.TryGetValue(providerId, out var index) ? index : 0;
        if (from >= messages.Count) return null;

        var lines = new List<string>();
        for (var i = from; i < messages.Count; i++)
        {
            var m = messages[i];
            var speaker = m.Role == "user" ? "User" : ProviderName(m.ProviderId);
            var text = m.Text.Length > MessageChars ? m.Text[..MessageChars] + " [...]" : m.Text;
            lines.Add($"{speaker}: {text}");
        }

        var body = string.Join("\n\n", lines);
        if (body.Length > HandoffChars) body = "[...]\n" + body[^HandoffChars..];
        return "Earlier in this OwnBridge conversation (some replies may be from another AI assistant). " +
               "Files may have changed since then, so re-read files before editing them.\n\n" + body;
    }

    public void MarkSeen(string providerId) => seenUpTo[providerId] = messages.Count;

    public string RenderTranscript()
    {
        var text = new StringBuilder("OwnBridge");
        foreach (var message in messages)
        {
            var speaker = message.Role == "user" ? "You" : ProviderName(message.ProviderId);
            text.Append("\n\n").Append(speaker).Append(": ").Append(message.Text);
        }
        return text.ToString();
    }

    public static string ProviderName(string providerId) => providerId switch
    {
        "chatgpt" => "ChatGPT",
        "gemini" => "Gemini",
        var other => other,
    };

    public void Dispose()
    {
        lockStream?.Dispose();
        lockStream = null;
    }
}

internal sealed record ConversationMessage(string Role, string ProviderId, string Text,
    DateTimeOffset CreatedUtc, string? ActiveFile = null);

// A solution (or folder) that owns its own conversations.
internal sealed class Workspace
{
    public Workspace(string root)
    {
        Root = root;
        SolutionFile = FindSolution(root);
        Key = (SolutionFile ?? root).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Key)))[..12].ToLowerInvariant();
        Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OwnBridge", "workspaces", hash);
    }

    public string Root { get; }

    public string? SolutionFile { get; }

    public string Key { get; }

    public string Folder { get; }

    public string ConversationsFolder => Path.Combine(Folder, "conversations");

    public string DisplayName => SolutionFile is not null ? Path.GetFileName(SolutionFile) : Root;

    public void SaveInfo()
    {
        var file = Path.Combine(Folder, "workspace.json");
        if (File.Exists(file)) return;
        Directory.CreateDirectory(Folder);
        File.WriteAllText(file, JsonSerializer.Serialize(new { root = Root, solution = SolutionFile }));
    }

    private static string? FindSolution(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.sln").Concat(Directory.EnumerateFiles(root, "*.slnx"))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

internal sealed record ConversationSummary(string Id, string Title, DateTime Updated)
{
    public static IReadOnlyList<ConversationSummary> List(Workspace workspace)
    {
        if (!Directory.Exists(workspace.ConversationsFolder)) return Array.Empty<ConversationSummary>();
        var result = new List<ConversationSummary>();
        foreach (var file in Directory.EnumerateFiles(workspace.ConversationsFolder, "*.jsonl"))
        {
            var title = "(empty)";
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    var message = JsonSerializer.Deserialize<ConversationMessage>(line);
                    if (message?.Role != "user") continue;
                    title = message.Text.Length > 60 ? message.Text[..60] + "..." : message.Text;
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                title = "(could not read)";
            }
            result.Add(new ConversationSummary(Path.GetFileNameWithoutExtension(file), title.ReplaceLineEndings(" "),
                File.GetLastWriteTime(file)));
        }
        return result.OrderByDescending(s => s.Updated).ToList();
    }
}
