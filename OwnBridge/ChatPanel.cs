using Microsoft.VisualStudio.Extensibility.UI;

namespace OwnBridge;

internal sealed class ChatPanel : RemoteUserControl
{
    public ChatPanel() : base(dataContext: new ChatPanelData())
    {
    }
}
