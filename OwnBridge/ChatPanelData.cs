using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;

namespace OwnBridge;

[DataContract]
internal sealed class ChatPanelData : NotifyPropertyChangedObject, IDisposable
{
    private readonly IChatProvider provider = new CodexChatProvider();
    private readonly ConversationSession session = new();
    private string prompt = string.Empty;
    private string transcript = "OwnBridge — Phase 2\n\nConnect your ChatGPT account to start chatting.";
    private string status = "Checking connection...";
    private bool busy;

    public ChatPanelData()
    {
        ConnectCommand = new AsyncCommand(async (_, cancellationToken) =>
        {
            if (busy) return;
            Busy = true;
            try
            {
                Status = "Checking ChatGPT sign-in...";
                Status = await provider.ConnectAsync(cancellationToken);
                Transcript = session.RenderTranscript() + $"\n\nOwnBridge: {Status}";
            }
            catch (Exception ex)
            {
                Status = $"Connection failed: {ex.Message}";
            }
            finally
            {
                Busy = false;
            }
        });

        SendCommand = new AsyncCommand(async (parameter, cancellationToken) =>
        {
            if (busy) return;
            var message = (parameter as string)?.Trim();
            if (string.IsNullOrWhiteSpace(message)) return;

            Busy = true;
            session.Add("user", provider.ProviderId, message);
            Prompt = string.Empty;
            var prefix = session.RenderTranscript() + "\n\nChatGPT: ";
            Transcript = prefix;

            try
            {
                Status = "Thinking...";
                var answer = await provider.SendAsync(session, message,
                    partial => Transcript = prefix + partial, cancellationToken);
                session.Add("assistant", provider.ProviderId, answer);
                Transcript = session.RenderTranscript();
                Status = "Connected to ChatGPT";
            }
            catch (Exception ex)
            {
                Transcript = session.RenderTranscript() + $"\n\nOwnBridge error: {ex.Message}";
                Status = "Could not complete the message. Your prompt is still in this chat.";
            }
            finally
            {
                Busy = false;
            }
        });

        _ = CheckConnectionAsync();
    }

    private async Task CheckConnectionAsync()
    {
        try
        {
            Status = await provider.GetStatusAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Status = $"ChatGPT unavailable: {ex.Message}";
        }
    }

    [DataMember]
    public string Prompt
    {
        get => prompt;
        set => SetProperty(ref prompt, value);
    }

    [DataMember]
    public string Transcript
    {
        get => transcript;
        set => SetProperty(ref transcript, value);
    }

    [DataMember]
    public string Status
    {
        get => status;
        set => SetProperty(ref status, value);
    }

    [DataMember]
    public bool Busy
    {
        get => busy;
        private set => SetProperty(ref busy, value);
    }

    [DataMember]
    public AsyncCommand ConnectCommand { get; }

    [DataMember]
    public AsyncCommand SendCommand { get; }

    public void Dispose() => provider.Dispose();
}
