using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace OwnBridge;

internal sealed class CodexChatProvider : IChatProvider
{
    private readonly CodexAppServerClient client = new();

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

    public async Task<string> ConnectAsync(CancellationToken cancellationToken)
    {
        if (await AccountTypeAsync(cancellationToken) == "chatgpt") return "Already connected to ChatGPT. You can send a message.";

        var completed = new TaskCompletionSource<(bool Success, string? Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? loginId = null;
        void OnNotification(string method, JsonElement data)
        {
            if (method != "account/login/completed" || data.ValueKind != JsonValueKind.Object) return;
            if (loginId is not null && data.GetProperty("loginId").GetString() != loginId) return;
            var success = data.TryGetProperty("success", out var value) && value.GetBoolean();
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
            loginId = result.GetProperty("loginId").GetString();
            var url = result.GetProperty("authUrl").GetString();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var browserUrl) ||
                browserUrl.Scheme != Uri.UriSchemeHttps ||
                (browserUrl.Host != "chatgpt.com" && browserUrl.Host != "auth.openai.com"))
                throw new InvalidOperationException("The sign-in address was not recognized.");

            Process.Start(new ProcessStartInfo(browserUrl.AbsoluteUri) { UseShellExecute = true });
            var outcome = await completed.Task.WaitAsync(TimeSpan.FromMinutes(3), cancellationToken);
            if (!outcome.Success) throw new InvalidOperationException(outcome.Error ?? "ChatGPT sign-in was cancelled.");
            if (await AccountTypeAsync(cancellationToken) != "chatgpt")
                throw new InvalidOperationException("Sign-in did not activate a ChatGPT account.");
            return "Connected to ChatGPT";
        }
        finally
        {
            client.Notification -= OnNotification;
        }
    }

    public async Task<string> SendAsync(ConversationSession session, string text,
        Action<string> onPartialAnswer, CancellationToken cancellationToken)
    {
        if (await AccountTypeAsync(cancellationToken) != "chatgpt")
            throw new InvalidOperationException("Select Connect ChatGPT and finish sign-in first.");

        var threadId = session.ThreadFor("chatgpt");
        if (threadId is null)
        {
            // Phase 2 is chat only; project/file context and edit approval come later.
            var newThread = await client.RequestAsync("thread/start", new
            {
                cwd = Path.GetTempPath(),
                sandbox = "read-only",
                approvalPolicy = "never",
                serviceName = "ownbridge",
            }, cancellationToken);
            threadId = newThread.GetProperty("thread").GetProperty("id").GetString()
                ?? throw new InvalidOperationException("The local AI client did not create a chat thread.");
            session.SetThread("chatgpt", threadId);
        }

        var done = new TaskCompletionSource<(string Status, string? Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var parts = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var ignored = new HashSet<string>(StringComparer.Ordinal);
        string answer = string.Empty;

        void OnNotification(string method, JsonElement data)
        {
            if (data.ValueKind != JsonValueKind.Object) return;
            if (data.TryGetProperty("threadId", out var notificationThread) &&
                notificationThread.GetString() != threadId) return;

            if (method == "item/started" && data.TryGetProperty("item", out var started) &&
                started.TryGetProperty("type", out var itemType) && itemType.GetString() == "agentMessage")
            {
                var id = started.GetProperty("id").GetString();
                if (id is null) return;
                if (started.TryGetProperty("phase", out var phase) && phase.ValueKind == JsonValueKind.String &&
                    phase.GetString() == "commentary") ignored.Add(id);
                else parts[id] = new StringBuilder();
            }
            else if (method == "item/agentMessage/delta" &&
                     data.TryGetProperty("itemId", out var itemId) &&
                     data.TryGetProperty("delta", out var delta))
            {
                var id = itemId.GetString();
                if (id is null || ignored.Contains(id)) return;
                if (!parts.TryGetValue(id, out var builder)) parts[id] = builder = new StringBuilder();
                builder.Append(delta.GetString());
                onPartialAnswer(builder.ToString());
            }
            else if (method == "item/completed" && data.TryGetProperty("item", out var item) &&
                     item.TryGetProperty("type", out var type) && type.GetString() == "agentMessage")
            {
                if (item.TryGetProperty("phase", out var phase) && phase.ValueKind == JsonValueKind.String &&
                    phase.GetString() == "commentary") return;
                if (item.TryGetProperty("text", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    answer = message.GetString() ?? "";
                    onPartialAnswer(answer);
                }
            }
            else if (method == "turn/completed" && data.TryGetProperty("turn", out var turn))
            {
                var status = turn.GetProperty("status").GetString() ?? "failed";
                var error = turn.TryGetProperty("error", out var failure) && failure.ValueKind == JsonValueKind.Object &&
                            failure.TryGetProperty("message", out var reason) ? reason.GetString() : null;
                done.TrySetResult((status, error));
            }
        }

        client.Notification += OnNotification;
        try
        {
            await client.RequestAsync("turn/start", new
            {
                threadId,
                input = new[] { new { type = "text", text } },
            }, cancellationToken);
            var outcome = await done.Task.WaitAsync(TimeSpan.FromMinutes(5), cancellationToken);
            if (outcome.Status != "completed")
                throw new InvalidOperationException(outcome.Error ?? $"The turn ended with status {outcome.Status}.");
            if (string.IsNullOrWhiteSpace(answer))
                throw new InvalidOperationException("The model finished without a text reply.");
            return answer;
        }
        finally
        {
            client.Notification -= OnNotification;
        }
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
