using System.Text;

namespace OwnBridge;

// A file attached to a chat message. Images go to the AI as pictures; everything else is sent as text.
internal sealed record ChatFile(string Path, bool IsImage)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

internal static class ChatFiles
{
    public const long MaxImageBytes = 10 * 1024 * 1024;
    private const long MaxTextFileBytes = 5 * 1024 * 1024;
    private const long MaxPdfBytes = 30 * 1024 * 1024;
    private const int MaxCharsPerFile = 60_000;
    private const int MaxTableRows = 400;

    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp",
    };

    public static bool IsImage(string path) => ImageTypes.Contains(Path.GetExtension(path));

    public static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/png",
    };

    // Checks a file before it is added to the message; returns a reason when it cannot be sent.
    public static string? Validate(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return $"File not found: {path}";
        if (IsImage(path)) return info.Length > MaxImageBytes ? $"{info.Name} is larger than 10 MB." : null;
        if (ZipAttachment.IsZip(path)) return ZipAttachment.Validate(info);
        if (info.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return info.Length > MaxPdfBytes ? $"{info.Name} is larger than 30 MB." : null;
        if (info.Length > MaxTextFileBytes) return $"{info.Name} is larger than 5 MB; attach a smaller file or a part of it.";
        return null;
    }

    // Screenshot from the clipboard, saved under %LOCALAPPDATA%\OwnBridge\images. Null when there is no image.
    public static string? SaveClipboardImage()
    {
        var png = Clipboard.TryGetImagePng();
        if (png is null) return null;
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OwnBridge", "images");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        File.WriteAllBytes(file, png);
        return file;
    }

    // The text files as one prompt block. Images are listed by name; the engines receive them separately.
    public static string? BuildTextBlock(IReadOnlyList<ChatFile> files, string workspaceRoot, bool insideSolution)
    {
        if (files.Count == 0) return null;
        var text = new StringBuilder();
        foreach (var file in files)
        {
            if (file.IsImage)
            {
                text.AppendLine($"Attached image: {file.Name} (look at it; for an error screenshot, find the cause in this solution).");
                continue;
            }
            if (ZipAttachment.IsZip(file.Path))
            {
                try { text.AppendLine(ZipAttachment.Describe(file.Path, workspaceRoot, insideSolution)); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    text.AppendLine($"Attached archive: {file.Name} — could not be unpacked ({ex.Message}).");
                }
                continue;
            }
            text.AppendLine($"Attached file: {file.Name} ({file.Path})");
            text.AppendLine("```");
            text.AppendLine(ReadAsText(file.Path));
            text.AppendLine("```");
        }
        return text.ToString().TrimEnd();
    }

    private static string ReadAsText(string path)
    {
        try
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            string content;
            if (extension is ".xlsx" or ".xlsm" or ".csv" or ".docx" or ".pdf")
            {
                var attachment = Attachment.Read(path);
                content = attachment.IsTable ? TableText(attachment) : attachment.Text;
            }
            else
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Take(8000).Any(b => b == 0)) return "(binary file — its content is not included)";
                content = File.ReadAllText(path);
            }
            content = content.Trim();
            return content.Length > MaxCharsPerFile ? content[..MaxCharsPerFile] + "\n[... truncated by OwnBridge]" : content;
        }
        catch (Exception ex)
        {
            return $"(could not read this file: {ex.Message})";
        }
    }

    private static string TableText(Attachment table)
    {
        var text = new StringBuilder();
        text.AppendLine("| " + string.Join(" | ", table.Headers) + " |");
        foreach (var row in table.Rows.Take(MaxTableRows))
            text.AppendLine("| " + string.Join(" | ", row.Select(c => c.ReplaceLineEndings(" "))) + " |");
        if (table.Rows.Count > MaxTableRows) text.AppendLine($"[... {table.Rows.Count - MaxTableRows} more rows]");
        return text.ToString();
    }
}
