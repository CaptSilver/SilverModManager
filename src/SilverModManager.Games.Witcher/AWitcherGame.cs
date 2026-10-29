using System.Collections.Immutable;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Generic;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Witcher;

public abstract class AWitcherGame<TSelf>(IServiceProvider provider) : AGame<TSelf>(provider)
    where TSelf : AWitcherGame<TSelf>, IGameData<TSelf>
{
    private StoreIdentifiers? _storeIdentifiers;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> LocationNames => WitcherConstants.LocationNames;

    protected abstract StoreIdentifiers CreateStoreIdentifiers();

    // Built on first read: the ids come from a virtual member and the base constructor runs before a derived one.
    public override StoreIdentifiers StoreIdentifiers => _storeIdentifiers ??= CreateStoreIdentifiers();

    /// <summary>The folder under Documents that the game keeps its settings in. Null when it keeps none there.</summary>
    protected virtual string? DocumentsFolderName => null;

    protected override ILibraryItemInstaller[] CreateInstallers(IServiceProvider provider) => [];

    protected override ILoadoutSynchronizer CreateSynchronizer(IServiceProvider provider) => new WitcherSynchronizer(provider, this);

    // Saves stay unmapped so that removing a loadout can never delete them.
    public override ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        var locations = ImmutableDictionary.CreateBuilder<LocationId, AbsolutePath>();
        locations.Add(LocationId.Game, gameLocatorResult.Path);
        if (DocumentsFolderName is { } folder)
            locations.Add(WitcherConstants.WitcherDocuments, fileSystem.GetKnownPath(KnownPath.MyDocumentsDirectory).Combine(folder));
        return locations.ToImmutable();
    }
}
