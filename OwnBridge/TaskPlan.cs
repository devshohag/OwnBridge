using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OwnBridge;

internal enum TaskState { Pending, Running, Done, Failed, Skipped }

internal sealed class PlanTask
{
    public int Number { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public TaskState State { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string Commit { get; set; } = string.Empty;
    public List<string> SourceRow { get; set; } = new();

    [JsonIgnore]
    public string Icon => State switch
    {
        TaskState.Pending => "⏳",
        TaskState.Running => "🔄",
        TaskState.Done => "✅",
        TaskState.Failed => "❌",
        _ => "⏭",
    };
}

// A list of tasks built from an attached file, saved next to its conversation.
internal sealed class TaskPlan
{
    public const int MaxTasks = 500;

    public string SourceFile { get; set; } = string.Empty;
    public List<string> Headers { get; set; } = new();
    public List<PlanTask> Tasks { get; set; } = new();

    [JsonIgnore]
    public string SourceName => Path.GetFileName(SourceFile);

    [JsonIgnore]
    public int DoneCount => Tasks.Count(t => t.State is TaskState.Done);

    [JsonIgnore]
    public PlanTask? NextPending => Tasks.FirstOrDefault(t => t.State == TaskState.Pending);

    // Tables: one task per row. Documents: one task per section (headings, "Phase N", numbered items).
    public static TaskPlan FromAttachment(Attachment attachment)
    {
        var plan = new TaskPlan { SourceFile = attachment.FilePath, Headers = attachment.Headers };
        if (attachment.IsTable)
        {
            var titleColumn = FindColumn(attachment.Headers, "title", "summary", "subject", "issue", "name", "task", "description");
            var keyColumn = FindColumn(attachment.Headers, "id", "#", "key", "ticket", "no", "number", "sl");
            var number = 0;
            foreach (var row in attachment.Rows.Take(MaxTasks))
            {
                number++;
                string Cell(int? column) => column is { } c && c < row.Count ? row[c] : string.Empty;
                var title = Cell(titleColumn);
                if (title.Length == 0) title = row.FirstOrDefault(v => v.Length > 0) ?? $"Row {number}";
                var details = string.Join("\n", attachment.Headers.Select((h, i) => i < row.Count && row[i].Length > 0 ? $"{h}: {row[i]}" : null)
                    .Where(line => line is not null));
                plan.Tasks.Add(new PlanTask
                {
                    Number = number,
                    Key = Cell(keyColumn),
                    Title = Shorten(title, 120),
                    Details = details,
                    SourceRow = row.ToList(),
                });
            }
            return plan;
        }

        var sections = SplitSections(attachment.Text);
        var index = 0;
        foreach (var (title, body) in sections.Take(MaxTasks))
        {
            index++;
            plan.Tasks.Add(new PlanTask { Number = index, Title = Shorten(title, 120), Details = body.Trim() });
        }
        return plan;
    }

    private static int? FindColumn(List<string> headers, params string[] names)
    {
        foreach (var name in names)
        {
            var index = headers.FindIndex(h => h.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) return index;
        }
        foreach (var name in names.Where(n => n.Length > 2))
        {
            var index = headers.FindIndex(h => h.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) return index;
        }
        return null;
    }

    private static List<(string Title, string Body)> SplitSections(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (var pattern in new[]
                 {
                     @"^##\s+(.+)$", @"^#\s+(.+)$",
                     @"^\s*((phase|step|task|stage|milestone)\s*[-:]?\s*\d+.*)$",
                     @"^(\d+[.)]\s+.+)$",
                 })
        {
            var regex = new Regex(pattern, RegexOptions.IgnoreCase);
            var sections = new List<(string, string)>();
            string? title = null;
            var body = new StringBuilder();
            foreach (var line in lines)
            {
                var match = regex.Match(line);
                if (match.Success)
                {
                    if (title is not null) sections.Add((title, body.ToString()));
                    title = match.Groups[1].Value.Trim();
                    body.Clear();
                }
                else if (title is not null) body.AppendLine(line);
            }
            if (title is not null) sections.Add((title, body.ToString()));
            if (sections.Count >= 2) return sections;
        }
        var first = lines.FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "Document";
        return new List<(string, string)> { (first, text) };
    }

    private static string Shorten(string text, int max)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length > max ? single[..max] + "..." : single;
    }

    public string BuildPrompt(PlanTask task)
    {
        var outline = new StringBuilder();
        foreach (var t in Tasks)
        {
            var line = $"{t.Number}. {t.Title}";
            if (outline.Length + line.Length > 3000) { outline.AppendLine("..."); break; }
            outline.AppendLine(line);
        }
        var key = task.Key.Length > 0 ? $" ({task.Key})" : string.Empty;
        return $"You are working through a task list from \"{SourceName}\" inside Visual Studio (OwnBridge).\n" +
               $"Outline of all tasks:\n{outline}\n" +
               $"Now do ONLY task {task.Number} of {Tasks.Count}{key}: {task.Title}\n\n{task.Details}\n\n" +
               "Do not start other tasks. File changes and commands will be shown to the user for approval. " +
               "When you are done, reply with a short summary: which files you changed, created or deleted, " +
               "or why the task could not be done.";
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static TaskPlan? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var plan = JsonSerializer.Deserialize<TaskPlan>(File.ReadAllText(path));
            if (plan is null) return null;
            // A task that was running when Visual Studio closed did not finish.
            foreach (var task in plan.Tasks.Where(t => t.State == TaskState.Running)) task.State = TaskState.Pending;
            return plan;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    // A new workbook next to the source: the original columns (for tables) plus OwnBridge results.
    public string ExportResults()
    {
        var folder = Path.GetDirectoryName(SourceFile) ?? Environment.CurrentDirectory;
        var file = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(SourceFile)}_OwnBridge_{DateTime.Now:yyyyMMdd-HHmm}.xlsx");
        var headers = Headers.Count > 0
            ? Headers.Concat(new[] { "OwnBridge Status", "OwnBridge Summary", "OwnBridge Commit" }).ToList()
            : new List<string> { "#", "Task", "Status", "Summary", "Commit" };
        var rows = Tasks.Select(t => (IReadOnlyList<string>)(Headers.Count > 0
            ? t.SourceRow.Concat(Enumerable.Repeat(string.Empty, Math.Max(0, Headers.Count - t.SourceRow.Count)))
                .Take(Headers.Count).Concat(new[] { t.State.ToString(), t.Summary, t.Commit }).ToList()
            : new List<string> { t.Number.ToString(), t.Title, t.State.ToString(), t.Summary, t.Commit }));
        Spreadsheet.Write(file, headers, rows);
        return file;
    }
}

// Commits only the files a task changed, so unrelated work in the repository is never included.
internal static class Git
{
    public static bool IsRepository(string root)
    {
        for (var dir = root; dir is not null; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"))) return true;
        return false;
    }

    public static async Task<string> CommitAsync(string root, IReadOnlyCollection<string> paths, string message, CancellationToken cancellationToken)
    {
        var git = EngineLocator.FindOnPath("git") ?? throw new InvalidOperationException("git was not found on PATH.");
        var add = new List<string> { "-C", root, "add", "-A", "--" };
        add.AddRange(paths);
        await RunAsync(git, add, cancellationToken);
        var commit = new List<string> { "-C", root, "commit", "-m", message, "--" };
        commit.AddRange(paths);
        await RunAsync(git, commit, cancellationToken);
        return (await RunAsync(git, new[] { "-C", root, "rev-parse", "--short", "HEAD" }, cancellationToken)).Trim();
    }

    private static async Task<string> RunAsync(string git, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = git,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git failed: {EngineLocator.Tail(await errors, 3)}");
        return await output;
    }
}
