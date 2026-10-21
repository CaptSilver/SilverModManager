using System.Collections.Immutable;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft;

/// <summary>
/// What loads mods into a FromSoft game. A game owns one, and the services the games share ask it instead of assuming
/// a particular loader: me3 does not support every FromSoft title, so the base cannot.
/// </summary>
public interface IFromSoftModLoader
{
    /// <summary>The locations the loader adds to the game's, such as the folders its mods and its own files deploy to.</summary>
    ImmutableDictionary<LocationId, AbsolutePath> Locations(FromSoftSettings settings, IFileSystem hostFileSystem, string installationFolder);

    /// <summary>The files the loader generates on every apply, such as its profile.</summary>
    IEnumerable<IIntrinsicFile> IntrinsicFiles();

    /// <summary>The installer for the loader's own release.</summary>
    ILibraryItemInstaller HostInstaller();

    /// <summary>The installers for what the loader loads: packages and natives.</summary>
    IEnumerable<ILibraryItemInstaller> ModInstallers();

    /// <summary>Whether the loadout changes what the game loads, which is what decides if it runs through the loader.</summary>
    bool IsModded(Loadout.ReadOnly loadout);

    /// <summary>Whether the loadout has the loader itself, enabled.</summary>
    bool HasHost(Loadout.ReadOnly loadout);
}
