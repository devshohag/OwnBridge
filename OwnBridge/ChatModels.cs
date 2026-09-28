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

    public MessageItem(bool isUser, string speaker)
    {
        IsUser = isUser;
        IsAssistant = !isUser;
        Speaker = speaker;
    }

    [DataMember] public bool IsUser { get; private set; }
    [DataMember] public bool IsAssistant { get; private set; }
    [DataMember] public string Speaker { get; private set; }
    [DataMember] public ObservableList<SegmentItem> Segments { get; } = new();
    [DataMember] public ObservableList<ActivityItem> Activities { get; } = new();

    [DataMember]
    public string StreamText { get => streamText; set => SetProperty(ref streamText, value); }

    [DataMember]
    public bool IsStreaming { get => isStreaming; set => SetProperty(ref isStreaming, value); }

    public void AddActivity(string line) => Activities.Add(ActivityItem.From(line));

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

    // Splits ``` fenced blocks into separate code segments.
    public void SetText(string text)
    {
        Segments.Clear();
        var buffer = new StringBuilder();
        var inCode = false;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                Flush(buffer, inCode);
                inCode = !inCode;
                continue;
            }
            buffer.Append(line).Append('\n');
        }
        Flush(buffer, inCode);
    }

    private void Flush(StringBuilder buffer, bool code)
    {
        var value = buffer.ToString().Trim('\n');
        buffer.Clear();
        if (value.Trim().Length > 0) Segments.Add(new SegmentItem(value, code));
    }
}

[DataContract]
internal sealed class SegmentItem
{
    public SegmentItem(string text, bool isCode)
    {
        Text = text;
        IsCode = isCode;
        IsText = !isCode;
    }

    [DataMember] public string Text { get; private set; }
    [DataMember] public bool IsCode { get; private set; }
    [DataMember] public bool IsText { get; private set; }
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
    public TaskRow(string line, AsyncCommand runCommand, AsyncCommand skipCommand)
    {
        Line = line;
        RunCommand = runCommand;
        SkipCommand = skipCommand;
    }

    [DataMember] public string Line { get; private set; }
    [DataMember] public AsyncCommand RunCommand { get; private set; }
    [DataMember] public AsyncCommand SkipCommand { get; private set; }
}
