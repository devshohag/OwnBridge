using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace OwnBridge;

internal sealed class CodexChatProvider : IChatProvider
{
    private readonly CodexAppServerClient client;
    private readonly ConcurrentDictionary<string, long> threadTokens = new(StringComparer.Ordinal);
    private JsonElement lastRateLimits;
    private string? model = Settings.Get("chatgptModel");
    private IReadOnlyList<string>? models;

    public string CurrentModel => model ?? "default";

    public async Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken)
    {
        if (models is not null) return models;
        var list = new List<string> { "default" };
        try
        {
            var result = await client.RequestAsync("model/list", new { }, cancellationToken);
            foreach (var name in new[] { "data", "models", "items" })
            {
                if (!result.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in array.EnumerateArray())
                {
                    var id = entry.ValueKind == JsonValueKind.String ? entry.GetString()
                        : Str(entry, "model") ?? Str(entry, "id") ?? Str(entry, "slug");
                    if (!string.IsNullOrWhiteSpace(id) && !list.Contains(id)) list.Add(id);
                }
                break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Older engines have no model list; "default" still works.
        }
        return models = list;
    }

    // The next message starts a new ChatGPT thread with this model.
    public void SetModel(string value)
    {
        var clean = value.Trim();
        model = clean.Length == 0 || clean == "default" ? null : clean;
        Settings.Set("chatgptModel", model ?? string.Empty);
    }
    private volatile string? currentThreadId;
    private volatile string? currentTurnId;

    public string ProviderId => "chatgpt";

    public string DisplayName => "ChatGPT";

    public Action<string>? Progress { get; set; }

    public CodexChatProvider()
    {
        client = new CodexAppServerClient(ct =>
            EngineLocator.ResolveAsync("ChatGPT", "codex", "@openai/codex", Progress, ct));
        client.Notification += OnUsageNotification;
    }

    // Token and limit updates arrive at any time; keep the latest values.
    private void OnUsageNotification(string method, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return;
        if (method == "thread/tokenUsage/updated" && Str(data, "threadId") is { } thread &&
            data.TryGetProperty("tokenUsage", out var usage) && usage.TryGetProperty("total", out var total) &&
            Num(total, "totalTokens") is { } tokens)
        {
            threadTokens[thread] = (long)tokens;
        }
        else if (method == "account/rateLimits/updated" && data.TryGetProperty("rateLimits", out var limits))
        {
            lastRateLimits = limits.Clone();
        }
    }

    public async Task<ProviderInfo> GetInfoAsync(ConversationSession? session, CancellationToken cancellationToken)
    {
        var account = "ChatGPT: not connected";
        try
        {
            var result = await client.RequestAsync("account/read", new { refreshToken = false }, cancellationToken);
            if (result.TryGetProperty("account", out var info) && info.ValueKind == JsonValueKind.Object)
            {
                var type = Str(info, "type");
                var email = Str(info, "email");
                var plan = Str(info, "planType");
                account = type == "chatgpt"
                    ? $"ChatGPT: {email ?? "signed in"}{(plan is null ? "" : $" ({plan})")}"
                    : "ChatGPT: API key sign-in (not your subscription)";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProviderInfo("ChatGPT: engine not running", ex.Message);
        }

        try
        {
            var result = await client.RequestAsync("account/rateLimits/read", null, cancellationToken);
            if (result.TryGetProperty("rateLimits", out var limits)) lastRateLimits = limits.Clone();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Older engines do not answer this request; notifications may still fill it in.
        }

        var parts = new List<string>();
        if (session?.ThreadFor("chatgpt") is { } threadId && threadTokens.TryGetValue(threadId, out var used))
            parts.Add($"This chat: {used:N0} tokens");
        parts.AddRange(DescribeLimits(lastRateLimits));
        return new ProviderInfo(account, parts.Count == 0 ? "Usage: shown after the first reply" : string.Join(" · ", parts));
    }

    private static IEnumerable<string> DescribeLimits(JsonElement limits)
    {
        if (limits.ValueKind != JsonValueKind.Object) yield break;
        foreach (var (name, fallback) in new[] { ("primary", "Short-term limit"), ("secondary", "Weekly limit") })
        {
            if (!limits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object) continue;
            if (Num(window, "usedPercent") is not { } percent) continue;
            var label = Num(window, "windowDurationMins") is { } minutes
                ? minutes >= 60 * 24 * 6 ? "Weekly limit" : minutes >= 60 ? $"{minutes / 60:0}-hour limit" : $"{minutes:0}-minute limit"
                : fallback;
            var reset = Num(window, "resetsAt") is { } unix
                ? $", resets {DateTimeOffset.FromUnixTimeSeconds((long)unix).ToLocalTime():ddd HH:mm}"
                : "";
            yield return $"{label}: {percent:0}% used{reset}";
        }
    }

    private static double? Num(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;

    public async Task<string> GetStatusAsync(CancellationToken cancellationToken)
    {
        var type = await AccountTypeAsync(cancellationToken);
        return type switch
        {
            "chatgpt" => "Connected to ChatGPT",
            "apiKey" => "Codex is using an API key. Connect with ChatGPT to use your subscription.",
            _ => "Not connected. Select Connect ChatGPT.",
        };
    }

    public async Task<string> ConnectAsync(Action<string> onStatus, CancellationToken cancellationToken)
    {
        if (await AccountTypeAsync(cancellationToken) == "chatgpt") return "Already connected to ChatGPT. You can send a message.";

        var completed = new TaskCompletionSource<(bool Success, string? Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? loginId = null;
        void OnNotification(string method, JsonElement data)
        {
            // Accept the login-completed notification, and treat any account update as a reason to re-check.
            if (method == "account/updated" || method == "authStatusChange")
            {
                completed.TrySetResult((true, null));
                return;
            }
            if (method != "account/login/completed" && method != "loginChatGptComplete") return;
            if (data.ValueKind != JsonValueKind.Object) { completed.TrySetResult((true, null)); return; }
            if (loginId is not null && data.TryGetProperty("loginId", out var id) &&
                id.ValueKind == JsonValueKind.String && id.GetString() != loginId) return;
            var success = !data.TryGetProperty("success", out var value) || value.ValueKind != JsonValueKind.False;
            var error = data.TryGetProperty("error", out var detail) && detail.ValueKind == JsonValueKind.String
                ? detail.GetString() : null;
            completed.TrySetResult((success, error));
        }

        client.Notification += OnNotification;
        try
        {
            var result = await client.RequestAsync("account/login/start", new
            {
                type = "chatgpt",
                useHostedLoginSuccessPage = true,
                appBrand = "chatgpt",
            }, cancellationToken);
            loginId = result.TryGetProperty("loginId", out var idValue) ? idValue.GetString() : null;
            var url = result.GetProperty("authUrl").GetString();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var browserUrl) ||
                browserUrl.Scheme != Uri.UriSchemeHttps ||
                (browserUrl.Host != "chatgpt.com" && browserUrl.Host != "auth.openai.com"))
                throw new InvalidOperationException("The sign-in address was not recognized.");

            Process.Start(new ProcessStartInfo(browserUrl.AbsoluteUri) { UseShellExecute = true });
            onStatus("Waiting for you to finish sign-in in the browser...");

            // Do not rely only on the notification: also poll the account every 2 seconds.
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (completed.Task.IsCompleted)
                {
                    var outcome = await completed.Task;
                    if (!outcome.Success) throw new InvalidOperationException(outcome.Error ?? "ChatGPT sign-in was cancelled.");
                }
                if (await AccountTypeAsync(cancellationToken) == "chatgpt") return "Connected to ChatGPT";
                if (completed.Task.IsCompleted) await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                else await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
            }
            throw new InvalidOperationException("Sign-in was not finished within 5 minutes. Select Connect ChatGPT to try again.");
        }
        finally
        {
            client.Notification -= OnNotification;
        }
    }

    public async Task<string> SendAsync(ConversationSession session, ChatTurnRequest request,
        IChatTurnObserver observer, CancellationToken cancellationToken)
    {
        if (await AccountTypeAsync(cancellationToken) != "chatgpt")
            throw new InvalidOperationException("Select Connect ChatGPT and finish sign-in first.");

        // One Codex thread per OwnBridge conversation (a conversation belongs to one solution).
        var threadKey = "chatgpt";
        var threadId = session.ThreadFor(threadKey);
        if (threadId is null)
        {
            // workspace-write + untrusted: the agent can read freely, but every file change
            // and every non-trivial command is sent to OwnBridge for approval first.
            var newThread = await client.RequestAsync("thread/start", new
            {
                cwd = request.WorkspaceRoot,
                model,
                sandbox = "workspace-write",
                approvalPolicy = "untrusted",
                serviceName = "ownbridge",
            }, cancellationToken);
            threadId = newThread.GetProperty("thread").GetProperty("id").GetString()
                ?? throw new InvalidOperationException("The local AI client did not create a chat thread.");
            session.SetThread(threadKey, threadId);
        }

        var done = new TaskCompletionSource<(string Status, string? Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var parts = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var ignored = new HashSet<string>(StringComparer.Ordinal);
        var items = new ConcurrentDictionary<string, JsonElement>(StringComparer.Ordinal);
        using var turnCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        string answer = string.Empty;

        void OnNotification(string method, JsonElement data)
        {
            if (data.ValueKind != JsonValueKind.Object) return;
            if (data.TryGetProperty("threadId", out var notificationThread) &&
                notificationThread.GetString() != threadId) return;

            if (method == "item/started" && data.TryGetProperty("item", out var started))
            {
                var id = Str(started, "id");
                var type = Str(started, "type");
                if (id is null) return;
                items[id] = started;
                if (type == "agentMessage")
                {
                    if (Str(started, "phase") == "commentary") ignored.Add(id);
                    else parts[id] = new StringBuilder();
                }
            }
            else if (method == "item/agentMessage/delta" &&
                     data.TryGetProperty("itemId", out var itemId) &&
                     data.TryGetProperty("delta", out var delta))
            {
                var id = itemId.GetString();
                if (id is null || ignored.Contains(id)) return;
                if (!parts.TryGetValue(id, out var builder)) parts[id] = builder = new StringBuilder();
                builder.Append(delta.GetString());
                observer.OnPartialAnswer(builder.ToString());
            }
            else if (method == "item/completed" && data.TryGetProperty("item", out var item))
            {
                var type = Str(item, "type");
                if (type == "agentMessage")
                {
                    if (Str(item, "phase") == "commentary") return;
                    var message = Str(item, "text");
                    if (message is not null)
                    {
                        answer = message;
                        observer.OnPartialAnswer(answer);
                    }
                }
                else if (type == "fileChange")
                {
                    var status = Str(item, "status") ?? "done";
                    foreach (var (path, _, _) in FileChanges(item))
                        observer.OnActivity($"{(status == "completed" ? "Edited" : $"File change {status}")}: {Rel(path, request.WorkspaceRoot)}");
                }
                else if (type == "commandExecution")
                {
                    var status = Str(item, "status") ?? "done";
                    var exit = item.TryGetProperty("exitCode", out var code) && code.ValueKind == JsonValueKind.Number
                        ? $" (exit {code.GetInt32()})" : "";
                    observer.OnActivity($"Command {status}: {CommandText(item)}{exit}");
                }
            }
            else if (method == "turn/completed" && data.TryGetProperty("turn", out var turn))
            {
                var status = Str(turn, "status") ?? "failed";
                var error = turn.TryGetProperty("error", out var failure) && failure.ValueKind == JsonValueKind.Object
                    ? Str(failure, "message") : null;
                done.TrySetResult((status, error));
            }
        }

        bool OnServerRequest(JsonElement requestId, string method, JsonElement data)
        {
            var approval = DescribeApproval(method, data, items, request.WorkspaceRoot);
            if (approval is null) return false; // Unknown request: the client declines it.

            _ = Task.Run(async () =>
            {
                var approved = false;
                try { approved = await observer.RequestApprovalAsync(approval, turnCancel.Token); }
                catch { approved = false; }
                var legacy = method is "execCommandApproval" or "applyPatchApproval";
                object result = legacy
                    ? new { decision = approved ? "approved" : "denied" }
                    : new { decision = approved ? "accept" : "decline" };
                try { await client.RespondAsync(requestId, result, CancellationToken.None); }
                catch { /* The client stopped; the turn will fail on its own. */ }
            });
            return true;
        }

        var prompt = request.BuildPrompt();

        client.Notification += OnNotification;
        client.ServerRequestHandler = OnServerRequest;
        try
        {
            var turnStart = await client.RequestAsync("turn/start", new
            {
                threadId,
                input = new[] { new { type = "text", text = prompt } },
            }, cancellationToken);
            currentThreadId = threadId;
            currentTurnId = turnStart.TryGetProperty("turn", out var t) ? Str(t, "id") : null;

            var outcome = await done.Task.WaitAsync(TimeSpan.FromMinutes(30), cancellationToken);
            if (outcome.Status == "interrupted")
                return string.IsNullOrWhiteSpace(answer) ? "(Stopped.)" : answer + "\n\n(Stopped.)";
            if (outcome.Status != "completed")
                throw new InvalidOperationException(outcome.Error ?? $"The turn ended with status {outcome.Status}.");
            if (string.IsNullOrWhiteSpace(answer))
                throw new InvalidOperationException("The model finished without a text reply.");
            return answer;
        }
        finally
        {
            turnCancel.Cancel(); // Any approval still waiting is declined.
            client.Notification -= OnNotification;
            client.ServerRequestHandler = null;
            currentThreadId = null;
            currentTurnId = null;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var threadId = currentThreadId;
        var turnId = currentTurnId;
        if (threadId is null || turnId is null) return;
        await client.RequestAsync("turn/interrupt", new { threadId, turnId }, cancellationToken);
    }

    private static ApprovalRequest? DescribeApproval(string method, JsonElement data,
        ConcurrentDictionary<string, JsonElement> items, string root)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        var reason = Str(data, "reason");
        var suffix = reason is null ? "" : $"\n\nReason: {reason}";

        if (method is "item/fileChange/requestApproval" or "applyPatchApproval")
        {
            var changes = new List<(string Path, string Kind, string? Diff)>();
            if (Str(data, "itemId") is { } itemId && items.TryGetValue(itemId, out var item))
                changes.AddRange(FileChanges(item));
            if (data.TryGetProperty("fileChanges", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
                foreach (var entry in legacy.EnumerateObject())
                    changes.Add((entry.Name, KindText(entry.Value), Str(entry.Value, "unified_diff") ?? Str(entry.Value, "diff")));

            var fileChanges = changes.Select(c => new FileChange(c.Path, FileChange.ActionFor(c.Kind))).ToList();
            var list = changes.Count == 0
                ? "(The engine did not list the files.)"
                : string.Join("\n", fileChanges.Select(c => $"{c.Action}: {Rel(c.Path, root)}"));
            var diff = string.Join("\n", changes.Where(c => !string.IsNullOrEmpty(c.Diff))
                .Select(c => $"--- {Rel(c.Path, root)}\n{c.Diff}"));
            return new ApprovalRequest("file", "ChatGPT wants to change files", list + suffix,
                string.IsNullOrWhiteSpace(diff) ? null : diff, fileChanges);
        }

        if (method is "item/commandExecution/requestApproval" or "execCommandApproval")
        {
            var command = CommandText(data);
            if (string.IsNullOrWhiteSpace(command) && Str(data, "itemId") is { } itemId &&
                items.TryGetValue(itemId, out var item))
                command = CommandText(item);
            var cwd = Str(data, "cwd");
            var detail = $"{(string.IsNullOrWhiteSpace(command) ? "(command not shown by the engine)" : command)}" +
                         (cwd is null ? "" : $"\n\nFolder: {cwd}") + suffix;
            return new ApprovalRequest("command", "ChatGPT wants to run a command", detail, null);
        }

        return null;
    }

    private static IEnumerable<(string Path, string Kind, string? Diff)> FileChanges(JsonElement item)
    {
        if (!item.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array) yield break;
        foreach (var change in changes.EnumerateArray())
        {
            var path = Str(change, "path");
            if (path is null) continue;
            yield return (path, change.TryGetProperty("kind", out var kind) ? KindText(kind) : "change", Str(change, "diff"));
        }
    }

    private static string KindText(JsonElement kind) => kind.ValueKind switch
    {
        JsonValueKind.String => kind.GetString() ?? "change",
        JsonValueKind.Object when Str(kind, "type") is { } type => type,
        JsonValueKind.Object => kind.EnumerateObject().Select(p => p.Name).FirstOrDefault() ?? "change",
        _ => "change",
    };

    private static string CommandText(JsonElement element)
    {
        if (!element.TryGetProperty("command", out var command)) return "";
        return command.ValueKind switch
        {
            JsonValueKind.String => command.GetString() ?? "",
            JsonValueKind.Array => string.Join(" ", command.EnumerateArray().Select(a => a.ToString())),
            _ => "",
        };
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Rel(string path, string root)
    {
        try { return Path.IsPathRooted(path) ? Path.GetRelativePath(root, path) : path; }
        catch { return path; }
    }

    private async Task<string?> AccountTypeAsync(CancellationToken cancellationToken)
    {
        var result = await client.RequestAsync("account/read", new { refreshToken = false }, cancellationToken);
        if (!result.TryGetProperty("account", out var account) || account.ValueKind == JsonValueKind.Null)
            return null;
        return account.GetProperty("type").GetString();
    }

    public void Dispose() => client.Dispose();
}
