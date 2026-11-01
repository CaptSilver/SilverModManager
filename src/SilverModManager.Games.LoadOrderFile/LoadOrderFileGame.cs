using NexusMods.Paths;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Generic;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>
/// A game whose mods are folders in a mod folder, loaded in the order an app-written file lists them, described by a
/// <see cref="LoadOrderFileGameDefinition"/>. Every such game is an instance of this class, so nothing may look one up
/// by type.
/// </summary>
public sealed class LoadOrderFileGame(IServiceProvider provider, LoadOrderFileGameDefinition definition)
    : DefinedGame<LoadOrderFileGameDefinition>(provider, definition), IGameIdentifyingFiles
{
    public IReadOnlyList<RelativePath> IdentifyingFiles => Definition.IdentifyingFiles;

    protected override ILoadoutSynchronizer CreateSynchronizer(IServiceProvider provider) => new LoadOrderFileSynchronizer(provider, Definition);

    protected override ILibraryItemInstaller[] CreateInstallers(IServiceProvider provider) =>
        SimpleInstallers.Create(provider, Definition.ModRoot, Definition.Install);
}
