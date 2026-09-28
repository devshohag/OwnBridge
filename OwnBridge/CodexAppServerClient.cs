using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace OwnBridge;

// The official local client owns authentication and all network requests.
// OwnBridge talks to its documented JSONL app-server protocol over stdio.
internal sealed class CodexAppServerClient : IDisposable
{
    private readonly SemaphoreSlim startupGate = new(1, 1);
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private Process? process;
    private int nextId;
    private bool initialized;
    private bool disposed;

    public event Action<string, JsonElement>? Notification;

    // Requests sent by the app-server to OwnBridge (for example approval prompts).
    // The handler must answer later with RespondAsync; it must not block the reader.
    public Func<JsonElement, string, JsonElement, bool>? ServerRequestHandler { get; set; }

    public async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await startupGate.WaitAsync(cancellationToken);
        try
        {
            if (disposed) throw new ObjectDisposedException(nameof(CodexAppServerClient));
            if (initialized && process is { HasExited: false }) return;
            if (process is not null)
            {
                if (!process.HasExited)
                    throw new InvalidOperationException("The local AI client is still starting. Try again shortly.");
                process.Dispose();
                process = null;
                initialized = false;
            }

            var start = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "codex",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            if (OperatingSystem.IsWindows())
            {
                // npm installs codex.cmd on Windows; cmd resolves it from PATH.
                start.ArgumentList.Add("/d");
                start.ArgumentList.Add("/c");
                start.ArgumentList.Add("codex app-server");
            }
            else
            {
                start.ArgumentList.Add("app-server");
            }

            try
            {
                process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the local AI client.");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new InvalidOperationException("Codex CLI was not found. Install it, then restart Visual Studio.", ex);
            }

            _ = ReadMessagesAsync(process);
            _ = process.StandardError.ReadToEndAsync(); // Drain diagnostics so the subprocess cannot block.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await RequestRawAsync("initialize", new
                {
                    clientInfo = new { name = "ownbridge", title = "OwnBridge", version = "0.3.0" },
                }, timeout.Token);
                await WriteAsync(new { method = "initialized", @params = new { } }, timeout.Token);
                initialized = true;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("The local AI client did not respond. Check that 'codex app-server' runs in PowerShell.", ex);
            }
        }
        finally
        {
            startupGate.Release();
        }
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
        return await RequestRawAsync(method, parameters, cancellationToken);
    }

    private async Task<JsonElement> RequestRawAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion)) throw new InvalidOperationException("Duplicate app-server request id.");
        try
        {
            await WriteAsync(new { method, id, @params = parameters }, cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    public Task RespondAsync(JsonElement id, object result, CancellationToken cancellationToken)
        => WriteAsync(new { id, result }, cancellationToken);

    // Older app-server builds use approved/denied; current builds use accept/decline.
    public static object DeclineResultFor(string method) =>
        method is "execCommandApproval" or "applyPatchApproval"
            ? new { decision = "denied" }
            : new { decision = "decline" };

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            if (process is null || process.HasExited)
                throw new InvalidOperationException("The local AI client is not running. Reopen OwnBridge.");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private async Task ReadMessagesAsync(Process running)
    {
        try
        {
            string? line;
            while ((line = await running.StandardOutput.ReadLineAsync()) is not null)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var id) && root.TryGetProperty("method", out var requestMethod))
                {
                    var requestId = id.Clone();
                    var name = requestMethod.GetString() ?? "";
                    var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;
                    var handled = false;
                    try { handled = ServerRequestHandler?.Invoke(requestId, name, parameters) ?? false; }
                    catch { handled = false; }
                    if (!handled)
                    {
                        // Fail closed: anything OwnBridge does not understand is declined.
                        await RespondAsync(requestId, DeclineResultFor(name), CancellationToken.None);
                    }
                }
                else if (root.TryGetProperty("id", out id) && id.ValueKind == JsonValueKind.Number)
                {
                    if (pending.TryRemove(id.GetInt32(), out var completion))
                    {
                        if (root.TryGetProperty("error", out var error))
                        {
                            var reason = error.TryGetProperty("message", out var value) ? value.GetString() : "Unknown protocol error";
                            completion.TrySetException(new InvalidOperationException(reason));
                        }
                        else if (root.TryGetProperty("result", out var result))
                        {
                            completion.TrySetResult(result.Clone());
                        }
                    }
                }
                else if (root.TryGetProperty("method", out var notificationMethod))
                {
                    var parameters = root.TryGetProperty("params", out var value) ? value.Clone() : default;
                    try { Notification?.Invoke(notificationMethod.GetString() ?? "", parameters); }
                    catch { /* A UI callback must not stop the protocol reader. */ }
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var entry in pending) entry.Value.TrySetException(ex);
        }
        finally
        {
            foreach (var entry in pending)
                entry.Value.TrySetException(new InvalidOperationException("The local AI client stopped. Reopen OwnBridge."));
        }
    }

    public void Dispose()
    {
        disposed = true;
        try
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        finally { process?.Dispose(); }
    }
}
