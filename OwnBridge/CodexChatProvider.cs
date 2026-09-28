using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace OwnBridge;

internal sealed class CodexChatProvider : IChatProvider
{
    private readonly CodexAppServerClient client = new();
    private volatile string? currentThreadId;
    private volatile string? currentTurnId;

    public string ProviderId => "chatgpt";

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

        // One Codex thread per solution folder, so a different solution gets a clean workspace.
        var threadKey = $"chatgpt|{request.WorkspaceRoot}";
        var threadId = session.ThreadFor(threadKey);
        if (threadId is null)
        {
            // workspace-write + untrusted: the agent can read freely, but every file change
            // and every non-trivial command is sent to OwnBridge for approval first.
            var newThread = await client.RequestAsync("thread/start", new
            {
                cwd = request.WorkspaceRoot,
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

        var prompt = request.Editor is null
            ? request.UserText
            : $"{request.Editor.ToPromptBlock(request.WorkspaceRoot)}\n\nUser request:\n{request.UserText}";

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

            var list = changes.Count == 0
                ? "(The engine did not list the files.)"
                : string.Join("\n", changes.Select(c => $"{c.Kind}: {Rel(c.Path, root)}"));
            var diff = string.Join("\n", changes.Where(c => !string.IsNullOrEmpty(c.Diff))
                .Select(c => $"--- {Rel(c.Path, root)}\n{c.Diff}"));
            return new ApprovalRequest("file", "ChatGPT wants to change files", list + suffix,
                string.IsNullOrWhiteSpace(diff) ? null : diff);
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
