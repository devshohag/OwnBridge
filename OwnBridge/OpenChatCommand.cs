using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace OwnBridge;

[VisualStudioContribution]
internal sealed class OpenChatCommand : Command
{
    public override CommandConfiguration CommandConfiguration => new("%OwnBridge.OpenChat.DisplayName%")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu],
        Icon = new(ImageMoniker.KnownValues.ToolWindow, IconSettings.IconAndText),
    };

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
        => this.Extensibility.Shell().ShowToolWindowAsync<ChatToolWindow>(activate: true, cancellationToken);
}
