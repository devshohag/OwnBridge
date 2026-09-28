using Microsoft.VisualStudio.Extensibility;

namespace OwnBridge;

[VisualStudioContribution]
internal sealed class OwnBridgeExtension : Extension
{
    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        Metadata = new(
            id: "OwnBridge.91631072-eedc-4bf2-af02-e31bc9f83f76",
            version: this.ExtensionAssemblyVersion,
            publisherName: "OwnBridge",
            displayName: "OwnBridge for Visual Studio",
            description: "ChatGPT coding agent in Visual Studio with your own account: solution context and approved edits."),
    };
}
