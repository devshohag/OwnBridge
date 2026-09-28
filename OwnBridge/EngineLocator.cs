using System.Collections.Concurrent;
using System.Diagnostics;

namespace OwnBridge;

// Finds an engine (Codex CLI, Gemini CLI). Order: the user's own install on PATH first,
// then OwnBridge's private copy; if neither exists, OwnBridge installs a private copy once.
internal static class EngineLocator
{
    private static readonly SemaphoreSlim installGate = new(1, 1);

    public static string EnginesRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OwnBridge", "engines");

    // Visual Studio may have started before Node/an engine was installed, so read the
    // current user and machine PATH from the registry as well as the process PATH.
    public static string CombinedPath
    {
        get
        {
            var parts = new[]
            {
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;
                foreach (var dir in part.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                    if (seen.Add(dir.Trim())) result.Add(dir.Trim());
            }
            return string.Join(Path.PathSeparator, result);
        }
    }

    public static string? FindOnPath(string name)
    {
        var extensions = OperatingSystem.IsWindows() ? new[] { ".cmd", ".exe", ".bat" } : new[] { "" };
        foreach (var dir in CombinedPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(dir, name + extension);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // Ignore malformed PATH entries.
                }
            }
        }
        return null;
    }

    public static string PrivateBin(string engine, string name) =>
        Path.Combine(EnginesRoot, engine, "node_modules", ".bin", OperatingSystem.IsWindows() ? name + ".cmd" : name);

    public static async Task<string> ResolveAsync(string engine, string name, string package,
        Action<string>? onStatus, CancellationToken cancellationToken)
    {
        var onPath = FindOnPath(name);
        if (onPath is not null) return onPath;
        var own = PrivateBin(engine, name);
        if (File.Exists(own)) return own;

        await installGate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(own)) return own;
            var npm = FindOnPath("npm") ?? throw new InvalidOperationException(
                $"The {engine} engine is not installed and Node.js was not found. Install Node.js 20 or newer from https://nodejs.org, restart Visual Studio, and try again.");

            onStatus?.Invoke($"Installing the {engine} engine for OwnBridge (one time, about a minute)...");
            var folder = Path.Combine(EnginesRoot, engine);
            Directory.CreateDirectory(folder);
            var (exitCode, errors) = await RunAsync(npm,
                $"install --prefix \"{folder}\" {package}@latest --no-fund --no-audit --loglevel=error",
                TimeSpan.FromMinutes(10), cancellationToken);
            if (!File.Exists(own))
                throw new InvalidOperationException(
                    $"Installing the {engine} engine failed (npm exit code {exitCode}). {errors}".Trim());
            onStatus?.Invoke($"The {engine} engine is installed.");
            return own;
        }
        finally
        {
            installGate.Release();
        }
    }

    // Starts a program; .cmd/.bat shims (npm) are started through cmd.exe with safe quoting.
    public static ProcessStartInfo CreateStartInfo(string executable, string arguments)
    {
        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var isShim = executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                     executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        if (isShim)
        {
            start.FileName = "cmd.exe";
            start.Arguments = $"/d /s /c \"\"{executable}\" {arguments}\"";
        }
        else
        {
            start.FileName = executable;
            start.Arguments = arguments;
        }
        start.Environment["PATH"] = CombinedPath;
        return start;
    }

    private static async Task<(int ExitCode, string Errors)> RunAsync(string executable, string arguments,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = CreateStartInfo(executable, arguments);
        start.RedirectStandardInput = false;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new InvalidOperationException("The engine installation took too long and was stopped.");
        }
        await output;
        return (process.ExitCode, Tail(await errors, 6));
    }

    public static string Tail(string text, int lines)
    {
        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        return string.Join(" | ", all.Skip(Math.Max(0, all.Length - lines)));
    }
}

// Keeps the last lines an engine wrote to stderr, so a failed start can show the real reason.
internal sealed class StderrTail
{
    private readonly ConcurrentQueue<string> lines = new();

    public ProtocolLog? Log { get; set; }

    public void Start(Process process) => _ = Task.Run(async () =>
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                lines.Enqueue(line.Trim());
                Log?.Write("stderr", line.Trim());
                while (lines.Count > 20) lines.TryDequeue(out _);
            }
        }
        catch
        {
            // The process ended; keep what was collected.
        }
    });

    public string Text(int count = 5)
    {
        var all = lines.ToArray();
        return string.Join(" | ", all.Skip(Math.Max(0, all.Length - count)));
    }
}
