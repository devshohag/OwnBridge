using System.Text;

namespace OwnBridge;

internal enum CommandKind { Read, Build, Run, Risky }

// Explains a command in plain words and decides whether it only reads.
// A command counts as read-only only when its program is on a short list AND it has no
// chaining, redirection or sub-commands outside quotes. Anything unclear is not read-only.
internal static class CommandSafety
{
    private static readonly HashSet<string> ReadPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "rg", "grep", "findstr", "select-string", "sls", "get-content", "gc", "cat", "type",
        "get-childitem", "gci", "ls", "dir", "head", "tail", "wc", "tree", "where", "which", "pwd",
        "get-location", "test-path", "resolve-path", "measure-object", "fd", "find",
    };

    private static readonly HashSet<string> ReadGitCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "diff", "log", "show", "blame", "grep", "ls-files", "rev-parse", "describe",
    };

    private static readonly HashSet<string> RiskyPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm", "del", "erase", "rd", "rmdir", "remove-item", "ri", "move-item", "mv", "move", "set-content",
        "out-file", "format", "shutdown", "reg", "sc", "icacls", "takeown", "curl", "wget", "invoke-webrequest",
        "iwr", "invoke-restmethod", "irm", "start-process", "powershell", "pwsh", "cmd",
    };

    public static (CommandKind Kind, string Label) Classify(string command)
    {
        var inner = Unwrap(command.Trim());
        if (inner.Length == 0) return (CommandKind.Run, "Runs a command");
        switch (Scan(inner))
        {
            case ScanResult.Operators: return (CommandKind.Risky, "Runs several commands or redirects output");
            case ScanResult.Unclear: return (CommandKind.Run, "Runs a command (OwnBridge could not analyse it; check it yourself)");
        }

        var words = Split(inner);
        if (words.Count == 0) return (CommandKind.Run, "Runs a command");
        var program = ProgramName(words[0]);

        if (program == "git" && words.Count > 1)
        {
            var sub = words[1];
            if (ReadGitCommands.Contains(sub)) return (CommandKind.Read, $"Reads git history (git {sub}); changes nothing");
            if (sub is "push" or "reset" or "clean" or "rebase" or "checkout" or "restore" or "rm" or "branch")
                return (CommandKind.Risky, $"Changes the repository (git {sub})");
            return (CommandKind.Run, $"Runs git {sub}");
        }
        if (program == "dotnet" && words.Count > 1 && words[1] is "build" or "test" or "restore" or "--version" or "--info")
            return (CommandKind.Build, $"Builds or tests the solution (dotnet {words[1]}); may take a while");
        if (program is "msbuild" or "nuget" or "npm" or "yarn" or "pnpm")
            return (CommandKind.Build, $"Runs {program}");
        if (ReadPrograms.Contains(program))
            return (CommandKind.Read, $"Searches or reads files ({program}); changes nothing");
        if (RiskyPrograms.Contains(program))
            return (CommandKind.Risky, $"Can change or delete files ({program})");
        return (CommandKind.Run, $"Runs {program}");
    }

    // Codex on Windows wraps commands as: "C:\...\powershell.exe" -Command "rg ...".
    private static string Unwrap(string command)
    {
        var words = Split(command);
        if (words.Count >= 3)
        {
            var program = ProgramName(words[0]);
            var flag = words[1].ToLowerInvariant();
            if ((program is "powershell" or "pwsh" && flag is "-command" or "-c" or "-noprofile") ||
                (program == "cmd" && flag is "/c" or "/d"))
            {
                var index = 1;
                while (index < words.Count && (words[index].StartsWith('-') || words[index].StartsWith('/')))
                {
                    var f = words[index].ToLowerInvariant();
                    index++;
                    if (f is "-command" or "-c" or "/c") break;
                }
                var rest = string.Join(" ", words.Skip(index));
                if (rest.Length >= 2 && rest[0] == '"' && rest[^1] == '"') rest = rest[1..^1];
                return rest.Replace("\\\"", "\"").Trim();
            }
        }
        return command;
    }

    private static string ProgramName(string word)
    {
        var name = word.Trim('"', '\'');
        name = Path.GetFileName(name.Replace('/', '\\').Split('\\').Last());
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private enum ScanResult { Clean, Operators, Unclear }

    private static ScanResult Scan(string text)
    {
        char? quote = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is not null)
            {
                if (c == quote) quote = null;
                continue;
            }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c is ';' or '&' or '|' or '>' or '<' or '`' or '\n' or '\r') return ScanResult.Operators;
            if (c == '$' && i + 1 < text.Length && text[i + 1] == '(') return ScanResult.Operators;
        }
        return quote is null ? ScanResult.Clean : ScanResult.Unclear; // Unbalanced quotes: unclear, never read-only.
    }

    private static List<string> Split(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        foreach (var c in text)
        {
            if (quote is not null)
            {
                current.Append(c);
                if (c == quote) quote = null;
            }
            else if (c is '"' or '\'') { quote = c; current.Append(c); }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(c);
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }
}
