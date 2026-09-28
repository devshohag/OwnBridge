using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;

namespace OwnBridge;

[VisualStudioContribution]
internal sealed class ChatToolWindow : ToolWindow
{
    private readonly ChatPanel content;

    public ChatToolWindow(VisualStudioExtensibility extensibility) : base(extensibility)
    {
        Title = "OwnBridge";
        content = new ChatPanel(extensibility);
    }

    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        Placement = ToolWindowPlacement.DocumentWell,
        AllowAutoCreation = false,
    };

    public override Task<IRemoteUserControl> GetContentAsync(CancellationToken cancellationToken)
        => Task.FromResult<IRemoteUserControl>(content);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            content.Dispose();
        }

        base.Dispose(disposing);
    }
}
