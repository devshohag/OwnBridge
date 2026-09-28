using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace OwnBridge;

// JSON-RPC 2.0 over stdio for agents that speak the Agent Client Protocol (ACP), such as Gemini CLI.
// The official CLI owns sign-in and all network traffic; OwnBridge only exchanges messages with it.
internal sealed class AcpClient : IDisposable
{
    private readonly string command; // Display name used in messages.
    private readonly Func<CancellationToken, Task<string>> resolveExecutable;
    private readonly Func<IDictionary<string, string>>? environment;
    private readonly string[] flagCandidates;
    private readonly StderrTail stderr = new();
    private readonly ProtocolLog? log;
    private readonly SemaphoreSlim startupGate = new(1, 1);
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private Process? process;
    private int nextId;
    private bool disposed;

    public AcpClient(string command, Func<CancellationToken, Task<string>> resolveExecutable,
        Func<IDictionary<string, string>>? environment, params string[] flagCandidates)
    {
        this.command = command;
        this.resolveExecutable = resolveExecutable;
        this.environment = environment;
        this.flagCandidates = flagCandidates;
    }

    public AcpClient(string command, Func<CancellationToken, Task<string>> resolveExecutable,
        Func<IDictionary<string, string>>? environment, ProtocolLog log, params string[] flagCandidates)
        : this(command, resolveExecutable, environment, flagCandidates)
    {
        this.log = log;
        stderr.Log = log;
    }

    public string? LogPath => log?.FilePath;

    public JsonElement InitializeResult { get; private set; }

    public event Action<string, JsonElement>? Notification;

    // Agent-to-client requests (permission prompts). Return false to have the client refuse it.
    public Func<JsonElement, string, JsonElement, bool>? ServerRequestHandler { get; set; }

    public async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await startupGate.WaitAsync(cancellationToken);
        try
        {
            if (disposed) throw new ObjectDisposedException(nameof(AcpClient));
            if (process is { HasExited: false }) return;
            process?.Dispose();
            process = null;

            var executable = await resolveExecutable(cancellationToken);
            Exception? last = null;
            foreach (var flag in flagCandidates)
            {
                try
                {
                    await StartWithFlagAsync(executable, flag, cancellationToken);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    last = ex;
                    KillProcess();
                }
            }
            throw new InvalidOperationException(
                $"{command} could not be started in IDE mode. {last?.Message}".Trim(), last);
        }
        finally
        {
            startupGate.Release();
        }
    }

    private async Task StartWithFlagAsync(string executable, string flag, CancellationToken cancellationToken)
    {
        var start = EngineLocator.CreateStartInfo(executable, flag);
        log?.Reset();
        log?.Write("start", $"{executable} {flag}");
        if (environment is not null)
            foreach (var (name, value) in environment())
                start.Environment[name] = value;

        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {command}.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"Could not start {command} at {executable}.", ex);
        }

        var running = process;
        // Each process gets its own pending table, so a failed first attempt cannot fail the retry.
        pending = new ConcurrentDictionary<int, TaskCompletionSource<JsonElement>>();
        _ = ReadMessagesAsync(running, pending);
        stderr.Start(running); // Drains diagnostics and keeps the last lines for error messages.

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            InitializeResult = await RequestAsync("initialize", new
            {
                protocolVersion = 1,
                clientCapabilities = new
                {
                    // The agent uses its own file tools; every write still asks for permission.
                    fs = new { readTextFile = false, writeTextFile = false },
                    terminal = false,
                },
            }, timeout.Token, ensureStarted: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{command} did not answer in IDE mode ({flag}). {stderr.Text()}".Trim());
        }
    }

    public Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
        => RequestAsync(method, parameters, cancellationToken, ensureStarted: true);

    private async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken, bool ensureStarted)
    {
        if (ensureStarted) await EnsureStartedAsync(cancellationToken);
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var table = pending;
        table[id] = completion;
        try
        {
            await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            table.TryRemove(id, out _);
        }
    }

    public async Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
        await WriteAsync(new { jsonrpc = "2.0", method, @params = parameters }, cancellationToken);
    }

    public Task RespondAsync(JsonElement id, object? result, CancellationToken cancellationToken)
        => WriteAsync(new { jsonrpc = "2.0", id, result }, cancellationToken);

    private Task RespondErrorAsync(JsonElement id, string message)
        => WriteAsync(new { jsonrpc = "2.0", id, error = new { code = -32601, message } }, CancellationToken.None);

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            if (process is null || process.HasExited)
                throw new InvalidOperationException($"{command} is not running. Reopen OwnBridge.");
            var json = JsonSerializer.Serialize(message);
            log?.Write("->", json);
            await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private async Task ReadMessagesAsync(Process running, ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> mine)
    {
        try
        {
            string? line;
            while ((line = await running.StandardOutput.ReadLineAsync()) is not null)
            {
                log?.Write("<-", line);
                if (string.IsNullOrWhiteSpace(line) || line[0] != '{') continue; // Ignore non-protocol output.
                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }

                using (document)
                {
                    var root = document.RootElement;
                    var hasId = root.TryGetProperty("id", out var id);
                    var hasMethod = root.TryGetProperty("method", out var methodValue);
                    var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;

                    if (hasId && hasMethod)
                    {
                        var requestId = id.Clone();
                        var name = methodValue.GetString() ?? "";
                        var handled = false;
                        try { handled = ServerRequestHandler?.Invoke(requestId, name, parameters) ?? false; }
                        catch { handled = false; }
                        if (!handled) await RespondErrorAsync(requestId, $"OwnBridge does not support {name}.");
                    }
                    else if (hasId && id.ValueKind == JsonValueKind.Number && mine.TryRemove(id.GetInt32(), out var completion))
                    {
                        if (root.TryGetProperty("error", out var error))
                        {
                            var reason = error.TryGetProperty("message", out var value) ? value.GetString() : "Unknown protocol error";
                            var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
                            completion.TrySetException(new AcpException(code, reason ?? "Unknown protocol error"));
                        }
                        else
                        {
                            completion.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
                        }
                    }
                    else if (hasMethod)
                    {
                        try { Notification?.Invoke(methodValue.GetString() ?? "", parameters); }
                        catch { /* A UI callback must not stop the protocol reader. */ }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var entry in mine) entry.Value.TrySetException(ex);
        }
        finally
        {
            foreach (var entry in mine)
                entry.Value.TrySetException(new InvalidOperationException($"{command} stopped. {stderr.Text()}".Trim()));
        }
    }

    private void KillProcess()
    {
        try
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        finally
        {
            process?.Dispose();
            process = null;
        }
    }

    public void Dispose()
    {
        disposed = true;
        KillProcess();
    }
}

internal sealed class AcpException : InvalidOperationException
{
    public AcpException(int code, string message) : base(message) => Code = code;

    public int Code { get; }
}
