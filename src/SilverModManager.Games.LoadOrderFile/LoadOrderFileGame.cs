using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games.LoadOrder;
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
    : DefinedGame<LoadOrderFileGameDefinition>(provider, definition), IGameIdentifyingFiles, ILoadOrderPageVisibility
{
    public IReadOnlyList<RelativePath> IdentifyingFiles => Definition.IdentifyingFiles;

    public bool HidesLoadOrderPage => !Definition.FileOrdersLoad;

    protected override ILoadoutSynchronizer CreateSynchronizer(IServiceProvider provider) => new LoadOrderFileSynchronizer(provider, Definition);

    protected override ILibraryItemInstaller[] CreateInstallers(IServiceProvider provider) =>
        SimpleInstallers.Create(provider, Definition.ModRoot, Definition.Install);

    /// <summary>
    /// The Workshop duplicate check needs a Workshop source, and the order checks need manifests to read; a game
    /// without either would only run an emitter that can never report.
    /// </summary>
    protected override IEnumerable<IDiagnosticEmitter> CreateEmitters(IServiceProvider provider)
    {
        foreach (var emitter in base.CreateEmitters(provider)) yield return emitter;

        if (Definition.ExternalMods.OfType<SteamWorkshopModSource>().Any())
            yield return provider.GetRequiredService<WorkshopDuplicateEmitter>();

        if (Definition.ManifestReader is not null)
        {
            yield return provider.GetRequiredService<ManifestOrderEmitter>();
            yield return provider.GetRequiredService<ManifestDependencyEmitter>();
        }
    }
}
