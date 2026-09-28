using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace OwnBridge;

// Gemini through the official Gemini CLI in ACP (IDE) mode. The CLI owns the Google sign-in.
internal sealed class GeminiChatProvider : IChatProvider
{
    private AcpClient client;
    private readonly ProtocolLog log = new("gemini");
    private readonly ConcurrentDictionary<string, long> sessionTokens = new(StringComparer.Ordinal);
    private volatile string? currentSessionId;
    private bool authenticatedWithKey;
    private readonly List<string> models = new();

    public string CurrentModel => Settings.GeminiModel;

    // Gemini CLI lists the models this key can use when a session starts.
    public Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken)
    {
        lock (models)
        {
            var list = models.Count > 0 ? models.ToList() : new List<string>();
            if (!list.Contains(CurrentModel)) list.Insert(0, CurrentModel);
            return Task.FromResult<IReadOnlyList<string>>(list);
        }
    }

    public GeminiChatProvider()
    {
        client = CreateClient();
    }

    public Action<string>? Progress { get; set; }

    // With a saved API key, Gemini CLI runs in API-key mode; otherwise Google sign-in is used.
    public bool UsesApiKey => Secrets.HasGeminiKey;

    // Newer CLI builds use --acp; older builds use --experimental-acp. Try both.
    private AcpClient CreateClient() => new("Gemini CLI",
        ct => EngineLocator.ResolveAsync("Gemini", "gemini", "@google/gemini-cli", Progress, ct),
        () =>
        {
            var env = new Dictionary<string, string>();
            if (Secrets.ReadGeminiKey() is { Length: > 0 } key)
            {
                env["GEMINI_API_KEY"] = key;
                env["GEMINI_MODEL"] = Settings.GeminiModel;
                // Overrides a Google sign-in choice saved earlier in ~/.gemini/settings.json.
                env["GEMINI_DEFAULT_AUTH_TYPE"] = "gemini-api-key";
            }
            return env;
        },
        log, $"--acp --model {Settings.GeminiModel}", $"--experimental-acp --model {Settings.GeminiModel}");

    // Changing the model restarts the engine; the next message starts a new Gemini session.
    public void SetModel(string model)
    {
        var clean = new string(model.Trim().Where(c => char.IsLetterOrDigit(c) || c is '-' or '.' or '_').ToArray());
        Settings.GeminiModel = clean.Length == 0 ? Settings.DefaultGeminiModel : clean;
        RestartEngine();
    }

    private void RestartEngine()
    {
        var old = client;
        client = CreateClient();
        authenticatedWithKey = false;
        old.Dispose();
    }

    // Saving or removing the key restarts the engine so it picks up the new mode.
    public void SetApiKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) Secrets.DeleteGeminiKey();
        else Secrets.SaveGeminiKey(key);
        var old = client;
        client = CreateClient();
        authenticatedWithKey = false;
        old.Dispose();
    }

    public string KeyHint()
    {
        var key = Secrets.ReadGeminiKey();
        return key is { Length: > 4 } ? $"API key ••••{key[^4..]}" : "API key";
    }

    public string ProviderId => "gemini";

    public string DisplayName => "Gemini";

    public Task<string> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (UsesApiKey) return Task.FromResult($"Gemini uses your {KeyHint()}. Send a message, or select Connect Gemini to check it.");
        return Task.FromResult(GoogleEmail() is not null
            ? "Google sign-in found. Personal Google accounts may be refused by Google; save a Gemini API key if so."
            : "Not connected. Save a Gemini API key (recommended) or select Connect Gemini for Google sign-in.");
    }

    // Gemini CLI records the signed-in email in google_accounts.json; tokens are never read.
    private static string? GoogleEmail()
    {
        try
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "google_accounts.json");
            if (!File.Exists(file)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return Str(document.RootElement, "active");
        }
        catch
        {
            return null;
        }
    }

    public Task<ProviderInfo> GetInfoAsync(ConversationSession? session, CancellationToken cancellationToken)
    {
        var account = UsesApiKey ? $"Gemini: {KeyHint()} · model {CurrentModel}" :
            GoogleEmail() is { } email ? $"Gemini: {email} (Google sign-in)" : "Gemini: not connected";
        var usage = session?.ThreadFor("gemini") is { } id && sessionTokens.TryGetValue(id, out var tokens)
            ? $"This chat: {tokens:N0} tokens · remaining limit is not reported by Gemini CLI"
            : "Usage: Gemini CLI does not report remaining limits";
        return Task.FromResult(new ProviderInfo(account, usage));
    }

    public async Task<string> ConnectAsync(Action<string> onStatus, CancellationToken cancellationToken)
    {
        onStatus("Starting Gemini CLI...");
        await client.EnsureStartedAsync(cancellationToken);
        await EnsureSignedInAsync(Path.GetTempPath(), onStatus, cancellationToken);
        return UsesApiKey ? $"Connected to Gemini ({KeyHint()})" : "Connected to Gemini (Google sign-in)";
    }

    // session/new fails with "authentication required" until the user has signed in;
    // authenticate then opens the official Google sign-in in the browser.
    private async Task<string> EnsureSignedInAsync(string cwd, Action<string> onStatus, CancellationToken cancellationToken)
    {
        if (UsesApiKey && !authenticatedWithKey)
        {
            try
            {
                using var keyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                keyTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                await client.RequestAsync("authenticate", new { methodId = AuthMethodId(apiKey: true) }, keyTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // No answer: continue; session/new below reports a clear error if sign-in is missing.
            }
            catch (AcpException)
            {
                // Some CLI builds pick up GEMINI_API_KEY without an explicit authenticate call.
            }
            authenticatedWithKey = true;
        }

        try
        {
            return await NewSessionAsync(cwd, cancellationToken);
        }
        catch (AcpException ex) when (ex.Code == -32000 || ex.Message.Contains("auth", StringComparison.OrdinalIgnoreCase))
        {
            if (UsesApiKey)
                throw new InvalidOperationException($"Gemini refused the saved API key: {ex.Message}");
            onStatus("Opening Google sign-in in your browser...");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            await client.RequestAsync("authenticate", new { methodId = AuthMethodId(apiKey: false) }, timeout.Token);
            onStatus("Signed in. Starting Gemini...");
            return await NewSessionAsync(cwd, cancellationToken);
        }
    }

    private string AuthMethodId(bool apiKey)
    {
        var init = client.InitializeResult;
        if (init.ValueKind == JsonValueKind.Object && init.TryGetProperty("authMethods", out var methods) &&
            methods.ValueKind == JsonValueKind.Array)
        {
            string? first = null;
            foreach (var method in methods.EnumerateArray())
            {
                var id = Str(method, "id");
                if (id is null) continue;
                first ??= id;
                var name = Str(method, "name") ?? "";
                var isKey = id.Contains("api", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("API key", StringComparison.OrdinalIgnoreCase);
                var isGoogle = id.Contains("oauth", StringComparison.OrdinalIgnoreCase) ||
                               name.Contains("Google", StringComparison.OrdinalIgnoreCase);
                if (apiKey ? isKey : isGoogle && !isKey) return id;
            }
            if (first is not null && !apiKey) return first;
        }
        return apiKey ? "gemini-api-key" : "oauth-personal";
    }

    private async Task<string> NewSessionAsync(string cwd, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        JsonElement result;
        try
        {
            result = await client.RequestAsync("session/new", new { cwd, mcpServers = Array.Empty<object>() }, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Gemini did not start a session within 90 seconds. Details: {client.LogPath}");
        }
        if (result.TryGetProperty("models", out var info) && info.ValueKind == JsonValueKind.Object &&
            info.TryGetProperty("availableModels", out var available) && available.ValueKind == JsonValueKind.Array)
        {
            lock (models)
            {
                models.Clear();
                foreach (var entry in available.EnumerateArray())
                    if (Str(entry, "modelId") is { Length: > 0 } id && id != "auto") models.Add(id);
            }
        }
        return Str(result, "sessionId") ?? throw new InvalidOperationException("Gemini did not create a session.");
    }

    public async Task<string> SendAsync(ConversationSession session, ChatTurnRequest request,
        IChatTurnObserver observer, CancellationToken cancellationToken)
    {
        await client.EnsureStartedAsync(cancellationToken);
        var sessionId = session.ThreadFor("gemini");
        if (sessionId is null)
        {
            try
            {
                sessionId = await EnsureSignedInAsync(request.WorkspaceRoot, observer.OnActivity, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                RestartEngine();
                throw;
            }
            session.SetThread("gemini", sessionId);
        }

        var answer = new StringBuilder();
        var tools = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        using var turnCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        void OnNotification(string method, JsonElement data)
        {
            if (method != "session/update" || Str(data, "sessionId") != sessionId) return;
            if (!data.TryGetProperty("update", out var update)) return;
            switch (Str(update, "sessionUpdate"))
            {
                case "agent_message_chunk":
                    if (update.TryGetProperty("content", out var content) && Str(content, "text") is { } text)
                    {
                        lock (answer) answer.Append(text);
                        string snapshot;
                        lock (answer) snapshot = answer.ToString();
                        observer.OnPartialAnswer(snapshot);
                    }
                    break;
                case "tool_call":
                    if (Str(update, "toolCallId") is { } callId)
                        tools[callId] = Str(update, "title") ?? Str(update, "kind") ?? "tool";
                    break;
                case "tool_call_update":
                    if (Str(update, "toolCallId") is { } updatedId && Str(update, "status") is { } status &&
                        status is "completed" or "failed")
                    {
                        var title = Str(update, "title") ?? (tools.TryGetValue(updatedId, out var known) ? known : "tool");
                        observer.OnActivity($"{title} ({status})");
                    }
                    break;
            }
        }

        bool OnServerRequest(JsonElement requestId, string method, JsonElement data)
        {
            if (method != "session/request_permission") return false;
            var approval = DescribePermission(data, request.WorkspaceRoot);
            _ = Task.Run(async () =>
            {
                var approved = false;
                try { approved = await observer.RequestApprovalAsync(approval, turnCancel.Token); }
                catch { approved = false; }
                try { await client.RespondAsync(requestId, PermissionResult(data, approved), CancellationToken.None); }
                catch { /* The CLI stopped; the turn fails on its own. */ }
            });
            return true;
        }

        client.Notification += OnNotification;
        client.ServerRequestHandler = OnServerRequest;
        currentSessionId = sessionId;
        try
        {
            var result = await client.RequestAsync("session/prompt", new
            {
                sessionId,
                prompt = new[] { new { type = "text", text = request.BuildPrompt() } },
            }, cancellationToken).WaitAsync(TimeSpan.FromMinutes(30), cancellationToken);

            RecordUsage(sessionId, result);
            string final;
            lock (answer) final = answer.ToString();
            if (Str(result, "stopReason") == "cancelled")
                return string.IsNullOrWhiteSpace(final) ? "(Stopped.)" : final + "\n\n(Stopped.)";
            if (string.IsNullOrWhiteSpace(final))
                throw new InvalidOperationException("Gemini finished without a text reply.");
            return final;
        }
        finally
        {
            turnCancel.Cancel(); // Any permission still waiting is refused.
            client.Notification -= OnNotification;
            client.ServerRequestHandler = null;
            currentSessionId = null;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var sessionId = currentSessionId;
        if (sessionId is null) return;
        await client.NotifyAsync("session/cancel", new { sessionId }, cancellationToken);
    }

    // Newer ACP builds may attach token usage to the prompt result; use it when present.
    private void RecordUsage(string sessionId, JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return;
        JsonElement usage = default;
        if (result.TryGetProperty("usage", out var direct)) usage = direct;
        else if (result.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
                 meta.TryGetProperty("usage", out var nested)) usage = nested;
        if (usage.ValueKind != JsonValueKind.Object) return;
        foreach (var name in new[] { "totalTokens", "total_tokens", "totalTokenCount" })
        {
            if (usage.TryGetProperty(name, out var value) && value.TryGetInt64(out var tokens))
            {
                sessionTokens.AddOrUpdate(sessionId, tokens, (_, previous) => previous + tokens);
                return;
            }
        }
    }

    private static ApprovalRequest DescribePermission(JsonElement data, string root)
    {
        var call = data.TryGetProperty("toolCall", out var tc) ? tc : default;
        var kind = Str(call, "kind") ?? "";
        var title = Str(call, "title") ?? "a tool";
        var diff = new StringBuilder();
        var files = new List<string>();
        var changes = new List<FileChange>();

        if (call.ValueKind == JsonValueKind.Object && call.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (Str(item, "type") != "diff" || Str(item, "path") is not { } path) continue;
                var relative = Rel(path, root);
                var action = string.IsNullOrEmpty(Str(item, "oldText")) ? "CREATE" : "EDIT";
                files.Add($"{action}: {relative}");
                changes.Add(new FileChange(path, action));
                diff.Append("--- ").Append(relative).Append('\n');
                foreach (var line in (Str(item, "oldText") ?? "").Split('\n'))
                    if (line.Length > 0) diff.Append("- ").Append(line.TrimEnd('\r')).Append('\n');
                foreach (var line in (Str(item, "newText") ?? "").Split('\n'))
                    diff.Append("+ ").Append(line.TrimEnd('\r')).Append('\n');
                diff.Append('\n');
            }
        }

        if (kind == "edit" || files.Count > 0)
        {
            var detail = files.Count > 0 ? string.Join("\n", files) : title;
            return new ApprovalRequest("file", "Gemini wants to change files", detail,
                diff.Length > 0 ? diff.ToString() : null, changes);
        }
        if (kind == "execute")
            return new ApprovalRequest("command", "Gemini wants to run a command", title, null);
        return new ApprovalRequest("tool", "Gemini wants to use a tool", title, null);
    }

    // Pick the agent's own "allow once" / "reject once" option; never "always".
    private static object PermissionResult(JsonElement data, bool approved)
    {
        string? chosen = null;
        if (data.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
        {
            var wanted = approved ? "allow_once" : "reject_once";
            foreach (var option in options.EnumerateArray())
                if (Str(option, "kind") == wanted) { chosen = Str(option, "optionId"); break; }
            if (chosen is null && !approved)
                foreach (var option in options.EnumerateArray())
                    if (Str(option, "kind") is { } k && k.StartsWith("reject", StringComparison.Ordinal))
                    { chosen = Str(option, "optionId"); break; }
        }
        return chosen is null
            ? new { outcome = new { outcome = "cancelled" } }
            : new { outcome = new { outcome = "selected", optionId = chosen } };
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Rel(string path, string root)
    {
        try { return Path.IsPathRooted(path) ? Path.GetRelativePath(root, path) : path; }
        catch { return path; }
    }

    public void Dispose() => client.Dispose();
}
