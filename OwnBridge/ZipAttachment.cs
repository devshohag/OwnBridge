using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace OwnBridge;

// A .zip attached to a chat message is unpacked where the AI can read it, and described in the prompt:
// the file list, plus the contents of small text files. Large or binary files stay on disk for the AI to open.
//
// Inside a solution it goes to <solution>\.ownbridge\attachments\ (kept out of git through .git\info\exclude),
// because Gemini only reads files under the workspace. Without a solution it goes under the general folder.
internal static class ZipAttachment
{
    private const long MaxZipBytes = 100L * 1024 * 1024;
    private const long MaxUnpackedBytes = 300L * 1024 * 1024;
    private const int MaxEntries = 3000;
    private const int MaxListedFiles = 400;
    private const int MaxInlineChars = 80_000;
    private const int MaxInlinePerFile = 20_000;

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", "packages", "__MACOSX",
    };

    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".json", ".xml", ".config", ".md", ".txt", ".sql",
        ".cshtml", ".razor", ".html", ".htm", ".css", ".scss", ".js", ".ts", ".tsx", ".jsx", ".yml", ".yaml", ".ps1",
        ".sh", ".py", ".java", ".kt", ".go", ".rs", ".php", ".rb", ".vb", ".fs", ".ini", ".env", ".csv", ".log", ".resx",
        ".editorconfig", ".gitignore", ".http", ".proto", ".graphql", ".dockerfile", ".toml",
    };

    public static bool IsZip(string path) => Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase);

    public static string? Validate(FileInfo info) =>
        info.Length > MaxZipBytes ? $"{info.Name} is larger than 100 MB; unzip it and attach the part you need." : null;

    // Unpacks (once per zip version) and returns the prompt block describing it.
    public static string Describe(string zipPath, string workspaceRoot, bool insideSolution)
    {
        var target = TargetFolder(zipPath, workspaceRoot, insideSolution);
        var files = Directory.Exists(target) ? ListFiles(target) : Unpack(zipPath, target);

        var text = new StringBuilder();
        text.AppendLine($"Attached archive: {Path.GetFileName(zipPath)} — unpacked to {target} ({files.Count} files).");
        text.AppendLine("Read any file from that folder when you need it. It is a copy for reference: do not edit it; " +
                        "put changes in the solution instead.");
        text.AppendLine("Files:");
        foreach (var file in files.Take(MaxListedFiles))
            text.AppendLine($"  {file.Relative}  ({Size(file.Bytes)})");
        if (files.Count > MaxListedFiles) text.AppendLine($"  ... and {files.Count - MaxListedFiles} more files");

        // Small text files are included directly, smallest first, until the budget is used.
        var budget = MaxInlineChars;
        var included = 0;
        foreach (var file in files.Where(f => TextTypes.Contains(Path.GetExtension(f.Relative)) && f.Bytes <= MaxInlinePerFile * 2L)
                                  .OrderBy(f => f.Bytes))
        {
            string content;
            try { content = File.ReadAllText(Path.Combine(target, file.Relative)); }
            catch (IOException) { continue; }
            if (content.Length > MaxInlinePerFile || content.Length > budget) continue;
            if (included == 0) text.AppendLine().AppendLine("Contents of the smaller text files:");
            text.AppendLine($"--- {file.Relative}").AppendLine("```").AppendLine(content.TrimEnd()).AppendLine("```");
            budget -= content.Length;
            included++;
        }
        if (included < files.Count)
            text.AppendLine($"({files.Count - included} file(s) are not shown above; open them from {target}.)");
        return text.ToString().TrimEnd();
    }

    private static string TargetFolder(string zipPath, string workspaceRoot, bool insideSolution)
    {
        var info = new FileInfo(zipPath);
        var stamp = $"{info.FullName.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp)))[..8].ToLowerInvariant();
        var name = Path.GetFileNameWithoutExtension(zipPath);
        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        if (name.Length > 60) name = name[..60];
        var parent = Path.Combine(workspaceRoot, ".ownbridge", "attachments");
        Directory.CreateDirectory(parent);
        if (insideSolution) KeepOutOfGit(workspaceRoot);
        return Path.Combine(parent, $"{name}-{hash}");
    }

    // Adds ".ownbridge/" to .git\info\exclude (a local ignore list that is never committed).
    private static void KeepOutOfGit(string root)
    {
        try
        {
            var info = Path.Combine(root, ".git", "info");
            if (!Directory.Exists(Path.Combine(root, ".git"))) return;
            Directory.CreateDirectory(info);
            var exclude = Path.Combine(info, "exclude");
            var lines = File.Exists(exclude) ? File.ReadAllLines(exclude) : Array.Empty<string>();
            if (lines.Any(l => l.Trim() is ".ownbridge/" or ".ownbridge" or "/.ownbridge/")) return;
            File.AppendAllText(exclude, (lines.Length > 0 && lines[^1].Length > 0 ? "\n" : "") + ".ownbridge/\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record UnpackedFile(string Relative, long Bytes);

    private static List<UnpackedFile> Unpack(string zipPath, string target)
    {
        var temp = target + ".partial";
        if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        Directory.CreateDirectory(temp);
        var root = Path.GetFullPath(temp) + Path.DirectorySeparatorChar;
        var files = new List<UnpackedFile>();
        long total = 0;
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            if (zip.Entries.Count > MaxEntries * 3) throw new InvalidDataException($"The archive has {zip.Entries.Count} entries; the limit is {MaxEntries}.");
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue; // Folder entry.
                var relative = entry.FullName.Replace('\\', '/');
                if (relative.Split('/').Any(SkippedFolders.Contains)) continue;
                var destination = Path.GetFullPath(Path.Combine(temp, relative));
                // "Zip slip": an entry such as ../../x must never land outside the target folder.
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                if (files.Count >= MaxEntries) break;
                total += entry.Length;
                if (total > MaxUnpackedBytes) throw new InvalidDataException("The archive unpacks to more than 300 MB; attach a smaller part of it.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
                files.Add(new UnpackedFile(Path.GetRelativePath(temp, destination), entry.Length));
            }
        }
        Directory.Move(temp, target);
        return files.OrderBy(f => f.Relative, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<UnpackedFile> ListFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => new UnpackedFile(Path.GetRelativePath(folder, f), new FileInfo(f).Length))
            .OrderBy(f => f.Relative, StringComparer.OrdinalIgnoreCase).ToList();

    private static string Size(long bytes) =>
        bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / 1024.0 / 1024.0:0.#} MB";
}
