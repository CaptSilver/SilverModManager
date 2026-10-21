using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Extensions;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FromSoft.Installers;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Settings;

namespace NexusMods.Games.FromSoft.Me3;

/// <summary>
/// The me3 mod loader of a game. The constructor only stores its arguments: the settings, the file system and the
/// version probe are fetched from the provider when a member is called, so a test that replaces one of them in the
/// container is honoured.
/// </summary>
public sealed class Me3Loader(IServiceProvider provider, Me3LoaderOptions options, FromSoftGameDefinition definition) : IFromSoftModLoader
{
    /// <summary>The profile text of the game: its me3 name, modded save and overlay natives.</summary>
    public Me3ProfileSpec ProfileSpec => new(options.GameName, definition.SaveNames.Modded, options.OverlayNatives);

    /// <summary>Whether the game loads its mods with me3.</summary>
    public static bool Serves(IGameData game) => game is IFromSoftGame { Loader: Me3Loader };

    /// <summary>The folder mod packages and natives of one installation deploy to.</summary>
    public static AbsolutePath ModsFolder(FromSoftGameLayout layout, FromSoftSettings settings, IFileSystem fileSystem, string installationFolder)
        => FromSoftPaths.InstallationRoot(layout, settings, fileSystem, installationFolder) / "mods";

    /// <summary>The folder the me3 release of one installation deploys to.</summary>
    public static AbsolutePath HostFolder(FromSoftGameLayout layout, FromSoftSettings settings, IFileSystem fileSystem, string installationFolder)
        => FromSoftPaths.InstallationRoot(layout, settings, fileSystem, installationFolder) / "me3";

    // The ids are persisted in every deployed loadout, so every me3 game uses these two.
    public ImmutableDictionary<LocationId, AbsolutePath> Locations(FromSoftSettings settings, IFileSystem hostFileSystem, string installationFolder)
        => ImmutableDictionary<LocationId, AbsolutePath>.Empty
            .Add(LocationId.Me3Mods, ModsFolder(definition.Layout, settings, hostFileSystem, installationFolder))
            .Add(LocationId.Me3Host, HostFolder(definition.Layout, settings, hostFileSystem, installationFolder));

    public IEnumerable<IIntrinsicFile> IntrinsicFiles()
        => [new Me3ProfileFile(provider.GetRequiredService<ILogger<Me3ProfileFile>>(), this)];

    public ILibraryItemInstaller HostInstaller() => new Me3HostInstaller(provider);

    public IEnumerable<ILibraryItemInstaller> ModInstallers() => [new Me3PackageInstaller(provider), new Me3NativeInstaller(provider)];

    /// <summary>True when any enabled file of the loadout sits in the mods folder: a package, a native or an overlay file.</summary>
    public bool IsModded(Loadout.ReadOnly loadout)
        => EnabledTargets(loadout).Any(path => path.LocationId == LocationId.Me3Mods);

    /// <summary>True when the loadout has the me3 binary, enabled, in the me3 host location.</summary>
    public bool HasHost(Loadout.ReadOnly loadout)
        => EnabledTargets(loadout).Contains(new GamePath(LocationId.Me3Host, Me3HostFiles.Binary));

    private static IEnumerable<GamePath> EnabledTargets(Loadout.ReadOnly loadout)
        => LoadoutItem.FindByLoadout(loadout.Db, loadout)
            .OfTypeLoadoutItemWithTargetPath()
            .Where(item => item.AsLoadoutItem().IsEnabled())
            .Select(item => (GamePath)item.TargetPath);
}
