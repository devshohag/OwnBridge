using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;

namespace OwnBridge;

// The active file and selection at the moment the user pressed Send.
internal sealed record EditorContext(string FilePath, string? SelectedText)
{
    private const int MaxSelectionChars = 20_000;

    public static async Task<EditorContext?> CaptureAsync(IClientContext clientContext, CancellationToken cancellationToken)
    {
        try
        {
            using var textView = await clientContext.GetActiveTextViewAsync(cancellationToken);
            if (textView is null) return null;

            var uri = textView.Document.Uri;
            if (uri is null || !uri.IsFile) return null;

            string? selected = textView.Selection.Extent.CopyToString();
            if (string.IsNullOrWhiteSpace(selected)) selected = null;
            else if (selected.Length > MaxSelectionChars)
                selected = selected[..MaxSelectionChars] + "\n[selection truncated by OwnBridge]";

            return new EditorContext(uri.LocalPath, selected);
        }
        catch
        {
            // Context is optional: if the editor cannot be read, the prompt is still sent.
            return null;
        }
    }

    // Walk up from the file to the folder that holds the solution (.sln/.slnx), else the git root.
    // Returns null for files that do not belong to a project: SDK and NuGet files, temp files,
    // anything under Program Files or AppData, and folders with no .sln/.slnx and no git repository.
    // Such a file (for example an SDK .targets file opened from a build error) must never become the workspace.
    public static string? FindWorkspaceRoot(string filePath)
    {
        if (IsSystemLocation(filePath)) return null;
        var start = Path.GetDirectoryName(filePath);
        for (var dir = start; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (Directory.EnumerateFiles(dir, "*.sln").Any() || Directory.EnumerateFiles(dir, "*.slnx").Any())
                return dir;
        }
        for (var dir = start; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"))) return dir;
        }
        return null;
    }

    public static bool IsSystemLocation(string filePath)
    {
        string full;
        try { full = Path.GetFullPath(filePath); }
        catch { return true; }
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetTempPath(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget"),
        };
        return roots.Any(r => r.Length > 0 &&
            full.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }

    public string ToPromptBlock(string workspaceRoot)
    {
        var relative = Path.GetRelativePath(workspaceRoot, FilePath);
        var text = $"Active file in Visual Studio: {relative}";
        if (SelectedText is not null)
            text += $"\nSelected text in that file:\n```\n{SelectedText}\n```";
        return text;
    }
}
