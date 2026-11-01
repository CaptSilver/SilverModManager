using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.DataModel;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tools;

/// <summary>Builds <see cref="ModdingTool"/>s with the services they run on.</summary>
public sealed class ModdingToolFactory(IToolRunner toolRunner, IProcessRunner processRunner, ToolArgumentResolver resolver, IPrefixResolver prefixResolver, ToolOutputCapture outputCapture)
{
    public ModdingTool Create(ToolDefinition definition, InstalledTool installed, GameId gameId)
        => new(definition, installed, gameId, toolRunner, processRunner, resolver, prefixResolver, outputCapture);
}
