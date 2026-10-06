using System.Runtime.Serialization;
using System.Text;
using Microsoft.VisualStudio.Extensibility.UI;

namespace OwnBridge;

// One chat message as the panel shows it: text and code blocks, plus the AI's activity lines.
[DataContract]
internal sealed class MessageItem : NotifyPropertyChangedObject
{
    private string streamText = string.Empty;
    private bool isStreaming;

    private const int CompactAfter = 4;
    private bool showDetails;
    private bool hasManyActivities;
    private bool showActivities = true;
    private string activitySummary = string.Empty;
    private int commands, edits, notes, others;

    public MessageItem(bool isUser, string speaker)
    {
        IsUser = isUser;
        IsAssistant = !isUser;
        Speaker = speaker;
        ToggleDetailsCommand = new AsyncCommand((_, _) =>
        {
            ShowDetails = !showDetails;
            UpdateSummary();
            return Task.CompletedTask;
        });
    }

    // Many steps collapse into one line ("9 commands · 2 files edited · Show details").
    [DataMember] public bool HasManyActivities { get => hasManyActivities; private set => SetProperty(ref hasManyActivities, value); }
    [DataMember] public bool ShowActivities { get => showActivities; private set => SetProperty(ref showActivities, value); }
    [DataMember] public bool ShowDetails { get => showDetails; private set => SetProperty(ref showDetails, value); }
    [DataMember] public string ActivitySummary { get => activitySummary; private set => SetProperty(ref activitySummary, value); }
    [DataMember] public AsyncCommand ToggleDetailsCommand { get; private set; }

    [DataMember] public bool IsUser { get; private set; }
    [DataMember] public bool IsAssistant { get; private set; }
    [DataMember] public string Speaker { get; private set; }
    [DataMember] public ObservableList<SegmentItem> Segments { get; } = new();
    [DataMember] public ObservableList<ActivityItem> Activities { get; } = new();

    [DataMember]
    public string StreamText { get => streamText; set => SetProperty(ref streamText, value); }

    [DataMember]
    public bool IsStreaming { get => isStreaming; set => SetProperty(ref isStreaming, value); }

    public void AddActivity(string line)
    {
        var clean = ActivityItem.Clean(line);
        if (clean is null) return;
        var item = ActivityItem.From(clean);
        Activities.Add(item);
        if (item.IsNote) notes++;
        else if (clean.StartsWith("Command", StringComparison.OrdinalIgnoreCase) ||
                 clean.StartsWith("Auto-approved", StringComparison.OrdinalIgnoreCase)) commands++;
        else if (clean.StartsWith("Edited", StringComparison.OrdinalIgnoreCase)) edits++;
        else others++;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var many = Activities.Count > CompactAfter;
        HasManyActivities = many;
        ShowActivities = !many || showDetails;
        var parts = new List<string>();
        if (commands > 0) parts.Add($"{commands} command{(commands == 1 ? "" : "s")}");
        if (edits > 0) parts.Add($"{edits} file{(edits == 1 ? "" : "s")} edited");
        if (others > 0) parts.Add($"{others} other step{(others == 1 ? "" : "s")}");
        if (notes > 0) parts.Add($"{notes} note{(notes == 1 ? "" : "s")}");
        ActivitySummary = $"{string.Join(" · ", parts)}  —  {(showDetails ? "Hide details" : "Show details")}";
    }

    // Stored assistant text = activity lines ("• ...") first, then the answer.
    public static MessageItem FromStored(ConversationMessage message)
    {
        var item = new MessageItem(message.Role == "user", message.Role == "user" ? "You" : ConversationSession.ProviderName(message.ProviderId));
        var lines = message.Text.Replace("\r\n", "\n").Split('\n');
        var index = 0;
        if (!item.IsUser)
        {
            while (index < lines.Length && lines[index].TrimStart().StartsWith("• ", StringComparison.Ordinal))
            {
                item.AddActivity(lines[index].TrimStart()[2..]);
                index++;
            }
        }
        item.SetText(string.Join("\n", lines.Skip(index)).Trim('\n'));
        return item;
    }

    // Splits ``` fenced blocks (with their language, e.g. ```powershell) into separate segments.
    public void SetText(string text)
    {
        Segments.Clear();
        var buffer = new StringBuilder();
        var inCode = false;
        var language = string.Empty;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                Flush(buffer, inCode, language);
                inCode = !inCode;
                language = inCode ? trimmed[3..].Trim() : string.Empty;
                continue;
            }
            buffer.Append(line).Append('\n');
        }
        Flush(buffer, inCode, language);
    }

    private void Flush(StringBuilder buffer, bool code, string language)
    {
        var value = buffer.ToString().Trim('\n');
        buffer.Clear();
        if (value.Trim().Length > 0) Segments.Add(new SegmentItem(value, code, language));
    }
}

// Text, a code block or a command block. Code and command blocks get a header and a Copy button.
[DataContract]
internal sealed class SegmentItem : NotifyPropertyChangedObject
{
    private static readonly HashSet<string> ShellLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell", "ps", "ps1", "pwsh", "bash", "sh", "shell", "zsh", "cmd", "bat", "batch", "console", "terminal",
    };

    private string copyLabel = "Copy";

    public SegmentItem(string text, bool isBlock, string language = "")
    {
        Text = text;
        IsText = !isBlock;
        IsCommand = isBlock && (ShellLanguages.Contains(language) || (language.Length == 0 && LooksLikeCommand(text)));
        IsCode = isBlock && !IsCommand;
        Label = IsCommand ? (language.Length > 0 ? language : "command") : (language.Length > 0 ? language : "code");
        CopyCommand = new AsyncCommand(async (_, _) =>
        {
            CopyLabel = Clipboard.TrySetText(Text) ? "Copied" : "Copy failed";
            await Task.Delay(TimeSpan.FromSeconds(2));
            CopyLabel = "Copy";
        });
    }

    [DataMember] public string Text { get; private set; }
    [DataMember] public bool IsText { get; private set; }
    [DataMember] public bool IsCode { get; private set; }
    [DataMember] public bool IsCommand { get; private set; }
    [DataMember] public string Label { get; private set; }
    [DataMember] public string CopyLabel { get => copyLabel; private set => SetProperty(ref copyLabel, value); }
    [DataMember] public AsyncCommand CopyCommand { get; private set; }

    // An unlabeled block whose first line starts like a shell command.
    private static bool LooksLikeCommand(string text)
    {
        var first = text.TrimStart().Split('\n')[0].TrimStart('$', '>', ' ');
        foreach (var start in new[] { "cd ", "dotnet ", "git ", "npm ", "npx ", "Get-", "Set-", "Remove-", "Expand-", "Copy-", "powershell", "winget ", "docker " })
            if (first.StartsWith(start, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

// A compact line such as "Searched ..." or "Edited ..."; Glyph is a Segoe MDL2 Assets icon.
[DataContract]
internal sealed class ActivityItem
{
    private ActivityItem(string glyph, string text, bool isNote)
    {
        Glyph = glyph;
        Text = text;
        IsNote = isNote;
    }

    [DataMember] public string Glyph { get; private set; }
    [DataMember] public string Text { get; private set; }
    [DataMember] public bool IsNote { get; private set; }

    private static readonly System.Text.RegularExpressions.Regex ShellWrapper = new(
        @"""?[A-Za-z]:\\[^""]*?(powershell|pwsh|cmd)(\.exe)?""?\s+(-NoProfile\s+)?(-Command|/c|/d /s /c)\s+",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Shortens an activity line for display: drops "Approval requested" noise (the card already asked),
    // removes the powershell.exe wrapper and doubled backslashes, and caps the length.
    public static string? Clean(string line)
    {
        var text = line.Trim();
        if (text.StartsWith("Approval requested", StringComparison.OrdinalIgnoreCase)) return null;
        text = text.Replace("\\\\", "\\");
        text = ShellWrapper.Replace(text, string.Empty);
        return text.Length > 180 ? text[..180] + "…" : text;
    }

    public static ActivityItem From(string line)
    {
        if (line.StartsWith("Note: ", StringComparison.Ordinal)) return new ActivityItem("", line[6..], true);
        var lower = line.ToLowerInvariant();
        var glyph =
            lower.StartsWith("edited") || lower.Contains("file change") ? "" :
            lower.StartsWith("approval") || lower.StartsWith("declined") ? "" :
            lower.StartsWith("approved") || lower.StartsWith("auto-approved") ? "" :
            lower.Contains("search") || lower.Contains("read") ? "" :
            lower.StartsWith("command") ? "" : "";
        return new ActivityItem(glyph, line, false);
    }
}

[DataContract]
internal sealed class DiffLine
{
    public DiffLine(string text)
    {
        Text = text;
        IsHeader = text.StartsWith("--- ", StringComparison.Ordinal) || text.StartsWith("+++ ", StringComparison.Ordinal) ||
                   text.StartsWith("@@", StringComparison.Ordinal);
        IsAdd = !IsHeader && text.StartsWith('+');
        IsDel = !IsHeader && text.StartsWith('-');
        IsContext = !IsHeader && !IsAdd && !IsDel;
    }

    [DataMember] public string Text { get; private set; }
    [DataMember] public bool IsAdd { get; private set; }
    [DataMember] public bool IsDel { get; private set; }
    [DataMember] public bool IsHeader { get; private set; }
    [DataMember] public bool IsContext { get; private set; }
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

    [DataMember] public string Title { get; private set; }
    [DataMember] public AsyncCommand OpenCommand { get; private set; }
}

// One row in the task list, with its own Run and Skip commands.
[DataContract]
internal sealed class TaskRow
{
    public TaskRow(string line, AsyncCommand runCommand, AsyncCommand skipCommand, string markLabel, AsyncCommand markCommand)
    {
        Line = line;
        RunCommand = runCommand;
        SkipCommand = skipCommand;
        MarkLabel = markLabel;
        MarkCommand = markCommand;
    }

    [DataMember] public string Line { get; private set; }
    [DataMember] public AsyncCommand RunCommand { get; private set; }
    [DataMember] public AsyncCommand SkipCommand { get; private set; }
    // "Mark done" or "Not done": for work finished outside the task runner, or a task that must be redone.
    [DataMember] public string MarkLabel { get; private set; }
    [DataMember] public AsyncCommand MarkCommand { get; private set; }
}

// A file waiting to be sent with the next message.
[DataContract]
internal sealed class AttachmentChip
{
    public AttachmentChip(string label, AsyncCommand removeCommand)
    {
        Label = label;
        RemoveCommand = removeCommand;
    }

    [DataMember] public string Label { get; private set; }
    [DataMember] public AsyncCommand RemoveCommand { get; private set; }
}
