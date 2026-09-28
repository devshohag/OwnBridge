using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;

namespace OwnBridge;

[DataContract]
internal sealed class ChatPanelData : NotifyPropertyChangedObject
{
    private string prompt = string.Empty;
    private string transcript = "OwnBridge — Phase 1\n\nThe chat panel is ready. AI connection comes in Phase 2.";

    public ChatPanelData()
    {
        SendCommand = new AsyncCommand((parameter, cancellationToken) =>
        {
            // CommandParameter captures the text in the Visual Studio process when Send is clicked.
            var message = (parameter as string)?.Trim();
            if (string.IsNullOrEmpty(message))
            {
                return Task.CompletedTask;
            }

            Transcript += $"\n\nYou: {message}\n\nOwnBridge: Phase 1 UI received your message. AI is not connected yet.";
            Prompt = string.Empty;
            return Task.CompletedTask;
        });
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
    public AsyncCommand SendCommand { get; }
}
