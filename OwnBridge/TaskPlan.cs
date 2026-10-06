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
    // Where the task lives in its file: the heading line (plans) or the row number (Excel/CSV).
    public string SourceLine { get; set; } = string.Empty;
    public int SourceRowNumber { get; set; } = -1;
    public string OriginalStatus { get; set; } = string.Empty;

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
    public int? StatusColumn { get; set; }
    // Text before the first task in a plan document (goals, rules). Sent with every task.
    public string Preamble { get; set; } = string.Empty;
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
            // A status column ("Dev Status", "Status", "State") that says Done/Closed/Fixed marks the row as finished.
            // "Dev" status wins over "QC" status, because the task is the development work.
            var statusColumn = FindStatusColumn(attachment.Headers);
            plan.StatusColumn = statusColumn;
            var number = 0;
            foreach (var row in attachment.Rows.Take(MaxTasks))
            {
                number++;
                string Cell(int? column) => column is { } c && c < row.Count ? row[c] : string.Empty;
                var title = Cell(titleColumn);
                if (title.Length == 0) title = row.FirstOrDefault(v => v.Length > 0) ?? $"Row {number}";
                var details = string.Join("\n", attachment.Headers.Select((h, i) => i < row.Count && row[i].Length > 0 ? $"{h}: {row[i]}" : null)
                    .Where(line => line is not null));
                var done = statusColumn is not null && DoneWords.Contains(Cell(statusColumn).Trim());
                plan.Tasks.Add(new PlanTask
                {
                    Number = number,
                    Key = Cell(keyColumn),
                    Title = Shorten(title, 120),
                    Details = details,
                    SourceRow = row.ToList(),
                    SourceRowNumber = number - 1 < attachment.RowNumbers.Count ? attachment.RowNumbers[number - 1] : -1,
                    OriginalStatus = Cell(statusColumn).Trim(),
                    State = done ? TaskState.Done : TaskState.Pending,
                    Summary = done ? $"Already {Cell(statusColumn).Trim()} in {attachment.Headers[statusColumn!.Value]}." : string.Empty,
                });
            }
            return plan;
        }

        var sections = SplitSections(attachment.Text);
        if (sections.Count > 1 && sections[0].Line.Length > 0)
        {
            var normalized = attachment.Text.Replace("\r\n", "\n");
            var at = normalized.IndexOf(sections[0].Line, StringComparison.Ordinal);
            if (at > 0) plan.Preamble = Shorten(normalized[..at].Trim(), 8000, keepLines: true);
        }
        var index = 0;
        foreach (var (title, body, line) in sections.Take(MaxTasks))
        {
            index++;
            // "[DONE]", "[COMPLETED]", "(done)" or a check mark in a heading = already finished.
            var done = DoneMarker.IsMatch(title);
            var clean = DoneMarker.Replace(title, string.Empty).Trim().TrimEnd('-', '—', ':').Trim();
            plan.Tasks.Add(new PlanTask
            {
                Number = index,
                Title = Shorten(clean.Length > 0 ? clean : title, 120),
                Details = body.Trim(),
                SourceLine = line,
                State = done ? TaskState.Done : TaskState.Pending,
                Summary = done ? "Marked done in the plan file." : string.Empty,
            });
        }
        return plan;
    }

    private static readonly Regex DoneMarker =
        new(@"\[(done|complete|completed)\]|\((done|complete|completed)\)|✅", RegexOptions.IgnoreCase);

    private static readonly Regex ProgressMarker =
        new(@"\s*(\[(done|complete|completed|partial|in progress|wip)\]|\((done|complete|completed|partial)\)|✅)", RegexOptions.IgnoreCase);

    private static readonly HashSet<string> DoneWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "done", "completed", "complete", "closed", "resolved", "fixed", "finished", "✅",
    };

    private static int? FindStatusColumn(List<string> headers)
    {
        var dev = headers.FindIndex(h => h.Contains("status", StringComparison.OrdinalIgnoreCase) &&
                                         h.Contains("dev", StringComparison.OrdinalIgnoreCase));
        if (dev >= 0) return dev;
        var any = headers.FindIndex(h => (h.Contains("status", StringComparison.OrdinalIgnoreCase) ||
                                          h.Trim().Equals("state", StringComparison.OrdinalIgnoreCase)) &&
                                         !h.Contains("qc", StringComparison.OrdinalIgnoreCase) &&
                                         !h.Contains("qa", StringComparison.OrdinalIgnoreCase));
        return any >= 0 ? any : null;
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

    private static List<(string Title, string Body, string Line)> SplitSections(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (var pattern in new[]
                 {
                     @"^##\s+(.+)$", @"^#\s+(.+)$",
                     // "Phase 22 — TTS", "Step 3: ...", "Task 4." — a separator (or nothing) must follow the number,
                     // so sentences such as "Phase 18 shipped two entities" are not headings.
                     @"^\s*((phase|step|task|stage|milestone)\s*[-:]?\s*\d+\s*([—–:\-.)]\s*.*)?)$",
                     @"^(\d+[.)]\s+.+)$",
                 })
        {
            var regex = new Regex(pattern, RegexOptions.IgnoreCase);
            var sections = new List<(string, string, string)>();
            string? title = null;
            var heading = string.Empty;
            var body = new StringBuilder();
            foreach (var line in lines)
            {
                var match = regex.Match(line);
                if (match.Success)
                {
                    if (title is not null) sections.Add((title, body.ToString(), heading));
                    title = match.Groups[1].Value.Trim();
                    heading = line;
                    body.Clear();
                }
                else if (title is not null) body.AppendLine(line);
            }
            if (title is not null) sections.Add((title, body.ToString(), heading));
            if (sections.Count >= 2) return sections;
        }
        var first = lines.FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "Document";
        return new List<(string, string, string)> { (first, text, string.Empty) };
    }

    private static string Shorten(string text, int max, bool keepLines)
    {
        if (!keepLines) return Shorten(text, max);
        return text.Length > max ? text[..max] + "\n[... shortened by OwnBridge]" : text;
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
        var rules = Preamble.Length > 0 ? $"General notes and rules from the plan (they apply to every task):\n{Preamble}\n\n" : string.Empty;
        return $"You are working through a task list from \"{SourceName}\" inside Visual Studio (OwnBridge).\n" + rules +
               $"Outline of all tasks:\n{outline}\n" +
               $"Now do ONLY task {task.Number} of {Tasks.Count}{key}: {task.Title}\n\n{task.Details}\n\n" +
               "Do not start other tasks. File changes and commands will be shown to the user for approval. " +
               "When you are done, reply with a short summary: which files you changed, created or deleted, " +
               "or why the task could not be done.";
    }

    // Writes the task's state into its own file: "[DONE]" on the plan heading, or "Done" in the
    // Excel/CSV status column. Returns null on success, otherwise a short reason for the status line.
    public string? WriteBack(PlanTask task, bool done)
    {
        try
        {
            var extension = Path.GetExtension(SourceFile).ToLowerInvariant();
            switch (extension)
            {
                case ".md" or ".markdown" or ".txt":
                    return WriteBackHeading(task, done);
                case ".xlsx" or ".xlsm":
                    if (StatusColumn is not { } column) return $"{SourceName} has no status column, so progress is kept in OwnBridge only.";
                    if (task.SourceRowNumber < 1) return "The row position is unknown; attach the file again.";
                    Spreadsheet.SetCell(SourceFile, task.SourceRowNumber, column, done ? "Done" : StatusWhenNotDone(task));
                    return null;
                case ".csv":
                    if (StatusColumn is not { } csvColumn) return $"{SourceName} has no status column, so progress is kept in OwnBridge only.";
                    if (task.SourceRowNumber < 0) return "The row position is unknown; attach the file again.";
                    Attachment.SetCsvCell(SourceFile, task.SourceRowNumber, csvColumn, done ? "Done" : StatusWhenNotDone(task));
                    return null;
                default:
                    return $"{extension} files are not updated; progress is kept in OwnBridge (Export results saves a report).";
            }
        }
        catch (IOException)
        {
            return $"Could not write {SourceName} — close it in Excel or other programs, then use Mark done again.";
        }
        catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return $"Could not update {SourceName}: {ex.Message}";
        }
    }

    private static string StatusWhenNotDone(PlanTask task) =>
        DoneWords.Contains(task.OriginalStatus) ? string.Empty : task.OriginalStatus;

    private string? WriteBackHeading(PlanTask task, bool done)
    {
        if (task.SourceLine.Length == 0) return "The plan heading is unknown; attach the file again.";
        var bytes = File.ReadAllBytes(SourceFile);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = File.ReadAllText(SourceFile);
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var index = Array.FindIndex(lines, l => l.TrimEnd() == task.SourceLine.TrimEnd());
        if (index < 0) return $"The heading \"{Shorten(task.SourceLine, 60)}\" was not found in {SourceName} (was it renamed?).";
        var clean = ProgressMarker.Replace(lines[index].TrimEnd(), string.Empty).TrimEnd();
        var updated = done ? clean + " [DONE]" : clean;
        if (updated == lines[index]) return null;
        lines[index] = updated;
        File.WriteAllText(SourceFile, string.Join(newline, lines), new UTF8Encoding(bom));
        task.SourceLine = updated;
        return null;
    }

    // Keeps progress from an earlier load of the same file: a task finished (or skipped) in OwnBridge stays so.
    public void MergeProgressFrom(TaskPlan? earlier)
    {
        if (earlier is null) return;
        string Id(PlanTask t) => t.Key.Length > 0 ? "k:" + t.Key.Trim() : "t:" + t.Title.Trim();
        var known = new Dictionary<string, PlanTask>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in earlier.Tasks) known.TryAdd(Id(t), t);
        foreach (var task in Tasks)
        {
            if (!known.TryGetValue(Id(task), out var old)) continue;
            if (task.Commit.Length == 0) task.Commit = old.Commit;
            if (task.State == TaskState.Pending && old.State is TaskState.Done or TaskState.Skipped)
            {
                task.State = old.State;
                task.Summary = old.Summary;
            }
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    // The task list belongs to the solution, not to one chat: New chat, History and restarts keep it.
    // workspaces\<id>\tasks\<hash of plan file>.json holds the progress; active.txt names the current plan.
    public static void SaveForWorkspace(TaskPlan plan, Workspace workspace)
    {
        plan.Save(workspace.PlanStatePath(plan.SourceFile));
        File.WriteAllText(workspace.ActivePlanPointer, plan.SourceFile);
    }

    public static TaskPlan? LoadForWorkspace(Workspace workspace)
    {
        try
        {
            if (!File.Exists(workspace.ActivePlanPointer)) return null;
            var source = File.ReadAllText(workspace.ActivePlanPointer).Trim();
            return source.Length == 0 ? null : Load(workspace.PlanStatePath(source));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static void RemoveForWorkspace(Workspace workspace)
    {
        try
        {
            if (File.Exists(workspace.ActivePlanPointer))
            {
                var source = File.ReadAllText(workspace.ActivePlanPointer).Trim();
                if (source.Length > 0 && File.Exists(workspace.PlanStatePath(source))) File.Delete(workspace.PlanStatePath(source));
                File.Delete(workspace.ActivePlanPointer);
            }
        }
        catch (IOException) { }
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
            StandardOutputEncoding = EngineLocator.Utf8,
            StandardErrorEncoding = EngineLocator.Utf8,
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
