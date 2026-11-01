using NexusMods.Paths;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Generic.Simple;

/// <summary>
/// A game described by a <see cref="SimpleGameDefinition"/>. Every simple game is an instance of this class, so
/// nothing may look one up by type.
/// </summary>
public sealed class SimpleGame(IServiceProvider provider, SimpleGameDefinition definition)
    : DefinedGame<SimpleGameDefinition>(provider, definition), IGameIdentifyingFiles, IToolVariableSource
{
    public IReadOnlyList<RelativePath> IdentifyingFiles => Definition.IdentifyingFiles;

    /// <summary>The folder mods go into is the one a tool is pointed at as its data folder.</summary>
    public IReadOnlyDictionary<string, GamePath> ToolVariables(Loadout.ReadOnly loadout) =>
        new Dictionary<string, GamePath> { [ToolVariableNames.DataFolder] = Definition.ModRoot.ToGamePath(RelativePath.Empty) };

    protected override ILoadoutSynchronizer CreateSynchronizer(IServiceProvider provider) => new SimpleGameSynchronizer(provider);

    protected override ILibraryItemInstaller[] CreateInstallers(IServiceProvider provider) =>
        SimpleInstallers.Create(provider, Definition.ModRoot, Definition.Install);
}
