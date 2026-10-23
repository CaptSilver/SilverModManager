using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FromSoft.Installers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Me3;

/// <summary>
/// Puts the me3 Linux release into a loadout. A release already in the library is used as it is; otherwise the
/// latest one is downloaded from me3's GitHub releases, checked against the digest GitHub publishes for it, added
/// to the library and installed through <see cref="Me3HostInstaller"/>, so the loadout keeps its own copy.
/// </summary>
public sealed class Me3HostProvisioner(IServiceProvider provider, ILogger<Me3HostProvisioner> logger)
{
    // One gate per loadout: a second trigger waits for the first and then finds the host already there.
    private readonly ConcurrentDictionary<LoadoutId, SemaphoreSlim> _gates = new();

    /// <summary>
    /// Does nothing when the loadout already has a file at <c>bin/me3</c> in the host location, enabled or not: a host
    /// the user disabled is theirs to enable. Throws when the release cannot be fetched or verified, and then nothing
    /// has been added to the library or the loadout.
    /// </summary>
    public async Task EnsureInstalled(LoadoutId loadoutId, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(loadoutId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var connection = provider.GetRequiredService<IConnection>();
            var loadout = Loadout.Load(connection.Db, loadoutId);
            if (!loadout.IsValid()) throw new InvalidOperationException($"The loadout {loadoutId} no longer exists.");
            var game = loadout.InstallationInstance.GetGame();
            if (!Me3Loader.Serves(game))
                throw new InvalidOperationException($"Loadout '{loadout.Name}' is for {game.DisplayName}, which does not load mods with me3, so it has no use for it.");
            if (HasHostFile(loadout)) return;

            var archive = FindInLibrary(connection.Db) ?? await Fetch(connection.Db, cancellationToken);
            logger.LogInformation("Installing me3 ({Archive}) into loadout '{Loadout}'", archive.AsLibraryFile().FileName, loadout.Name);

            var installer = game.LibraryItemInstallers.OfType<Me3HostInstaller>().Single();
            await provider.GetRequiredService<ILoadoutManager>()
                .InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadoutId, installer: installer);
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool HasHostFile(Loadout.ReadOnly loadout)
        => LoadoutItem.FindByLoadout(loadout.Db, loadout)
            .OfTypeLoadoutItemWithTargetPath()
            .Any(item => (GamePath)item.TargetPath == new GamePath(LocationId.Me3Host, Me3HostFiles.Binary));

    /// <summary>The newest archive in the library that is the Linux release.</summary>
    private static LibraryArchive.ReadOnly? FindInLibrary(IDb db)
    {
        // A name check first: reading an archive's tree for every archive in a big library would be slow.
        foreach (var archive in LibraryArchive.All(db)
                     .Where(archive => archive.AsLibraryFile().FileName.ToString().EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(archive => archive.Id.Value))
        {
            if (Me3HostInstaller.Accepts(archive)) return archive;
        }

        return null;
    }

    private async Task<LibraryArchive.ReadOnly> Fetch(IDb db, CancellationToken cancellationToken)
    {
        logger.LogInformation("Downloading the latest me3 Linux release");
        var file = await provider.GetRequiredService<IGitHubReleaseAssetSource>()
            .AddLatestAssetAsync(Me3HostFiles.RepositoryId, Me3HostFiles.ReleaseAssetSuffix, Me3HostFiles.ReleaseAssetHint, cancellationToken);

        // The installer would fall back to the manual one for an archive it refuses; say what was wrong instead.
        var archive = LibraryArchive.Load(file.Db, file.Id);
        if (!archive.IsValid() || !Me3HostInstaller.Accepts(archive))
            throw new InvalidOperationException($"The downloaded {file.FileName} is not the me3 Linux release: it lacks {string.Join(", ", Me3HostFiles.ReleaseFiles)}.");
        return archive;
    }
}
