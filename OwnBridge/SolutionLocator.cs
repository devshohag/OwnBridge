using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.ProjectSystem.Query;

namespace OwnBridge;

// Reads the solution that is open in this Visual Studio window, so OwnBridge knows the
// workspace even when no file is open in the editor.
internal static class SolutionLocator
{
    public static async Task<string?> GetSolutionFolderAsync(VisualStudioExtensibility extensibility, CancellationToken cancellationToken)
    {
        try
        {
            var results = await extensibility.Workspaces().QuerySolutionAsync(
                solution => solution.With(s => s.Path),
                cancellationToken);
            foreach (var solution in results)
            {
                if (string.IsNullOrWhiteSpace(solution.Path)) continue;
                var folder = Path.GetDirectoryName(solution.Path);
                if (string.IsNullOrWhiteSpace(folder)) continue;
                folder = folder.TrimEnd('\\', '/');
                if (Directory.Exists(folder) && !EditorContext.IsSystemLocation(Path.Combine(folder, "x")))
                    return folder;
            }
        }
        catch
        {
            // No solution, or the query is not available: the caller falls back to the open file.
        }
        return null;
    }

    // Used for chats when no solution and no project file is open. Only this empty folder is writable.
    public static string GeneralFolder
    {
        get
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OwnBridge", "general");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    public static bool IsGeneral(string root) =>
        string.Equals(Path.GetFullPath(root).TrimEnd('\\', '/'), GeneralFolder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
