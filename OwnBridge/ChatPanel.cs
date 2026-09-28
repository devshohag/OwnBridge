using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.UI;

namespace OwnBridge;

internal sealed class ChatPanel : RemoteUserControl
{
    private readonly ChatPanelData data;

    public ChatPanel(VisualStudioExtensibility extensibility) : this(new ChatPanelData(extensibility))
    {
    }

    private ChatPanel(ChatPanelData data) : base(dataContext: data)
    {
        this.data = data;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) data.Dispose();
        base.Dispose(disposing);
    }
}
