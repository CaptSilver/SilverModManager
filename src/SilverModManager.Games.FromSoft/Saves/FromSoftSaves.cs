using Microsoft.Extensions.Logging;
using NexusMods.Games.FromSoft.Launch;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Settings;

namespace NexusMods.Games.FromSoft.Saves;

/// <param name="Folder">The save folder of one Steam account.</param>
/// <param name="SteamId">The folder's name, the account's Steam id.</param>
public sealed record SaveFolder(string Folder, string SteamId, IReadOnlyList<SaveFileEntry> Files);

/// <param name="Problem">Why no folder could be listed, empty when one could (an empty list then means no saves yet).</param>
public sealed record SaveFolderListing(string Problem, IReadOnlyList<SaveFolder> Folders);

/// <summary>
/// What the saves page works with: the save folders of an installation's prefix, and backup, restore and quarantine
/// of the files in them. Each operation is limited to a folder the listing returned and to files
/// <see cref="SaveFolderScanner"/> knows.
/// </summary>
public sealed class FromSoftSaves(
    ISettingsManager settings,
    IFileSystem fileSystem,
    IPrefixResolver prefixes,
    IProcessTracker processTracker,
    TimeProvider clock,
    ILogger<FromSoftSaves> logger)
{
    public bool IsSteamRunning() => processTracker.IsSteamRunning();

    public SaveBackups BackupsOf(GameInstallation installation) => SaveBackups.For(settings, fileSystem, installation, clock);

    /// <summary>The launch gate's record of save folders it could not restore; the page offers to resolve a tainted one.</summary>
    public SaveLedger LedgerOf(GameInstallation installation) => SaveLedger.For(settings.Get<FromSoftSettings>(), fileSystem, installation);

    public SaveFolderListing List(GameInstallation installation)
    {
        var resolution = prefixes.Resolve(installation);
        if (!resolution.IsResolved) return new SaveFolderListing(resolution.Diagnostic ?? "The game's prefix could not be found.", []);

        var game = SaveBackups.FromSoftGameOf(installation);
        var scanner = new SaveFolderScanner(game.SaveNames);
        var root = FromSoftPaths.SaveRoot(game.Layout, resolution.Prefix.Path).GetFullPath();
        try
        {
            if (!Directory.Exists(root)) return new SaveFolderListing("", []);

            var folders = Directory.EnumerateDirectories(root)
                .Where(d => IsAccountFolder(game.Layout, Path.GetFileName(d)))
                .Order(StringComparer.Ordinal)
                .Select(d => new SaveFolder(d, Path.GetFileName(d), scanner.Scan(d)))
                .ToArray();
            return new SaveFolderListing("", folders);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The prefix can sit on a drive that is not mounted.
            logger.LogWarning(exception, "Could not list the save folders in {Folder}", root);
            return new SaveFolderListing($"The save folder {root} could not be read: {exception.Message}", []);
        }
    }

    /// <summary>What Steam Cloud holds for the installation's Steam, empty when the Steam folder is not known.</summary>
    public IReadOnlyList<CloudSave> CloudSaves(GameInstallation installation)
    {
        var game = SaveBackups.FromSoftGameOf(installation);
        // Steam Cloud's remote paths start with the game's folder under the save root.
        var provider = installation.LocatorResult.LinuxCompatabilityDataProvider;
        return provider is null
            ? []
            : SteamCloudSaves.Discover(provider.SteamInstallPath, game.Layout.SteamAppId, game.Layout.SaveFolderName, game.DisplayName, logger);
    }

    public SaveBackup Backup(GameInstallation installation, SaveFolder folder, SaveFileEntry save, string label = "")
    {
        RequireListed(installation, folder);
        return BackupsOf(installation).Backup(save.Path, label);
    }

    public RestoreResult Restore(GameInstallation installation, SaveFolder folder, SaveBackup backup)
    {
        RequireListed(installation, folder);
        return BackupsOf(installation).Restore(backup, folder.Folder);
    }

    public QuarantineResult Quarantine(GameInstallation installation, SaveFolder folder, SaveFileEntry save)
    {
        RequireListed(installation, folder);
        return new SaveQuarantine(BackupsOf(installation), processTracker).Quarantine(save.Path, CloudSaves(installation));
    }

    /// <summary>Whether a folder of the save root is one account's save folder, by the rule of the layout's account folder kind.</summary>
    internal static bool IsAccountFolder(FromSoftGameLayout layout, string folderName) => layout.AccountFolder switch
    {
        SaveAccountFolderKind.SteamId64Decimal => ulong.TryParse(folderName, out _),
        _ => throw new ArgumentOutOfRangeException(nameof(layout), layout.AccountFolder, "Unknown account folder kind"),
    };

    private void RequireListed(GameInstallation installation, SaveFolder folder)
    {
        if (List(installation).Folders.All(f => f.Folder != folder.Folder))
            throw new SaveRefusedException($"{folder.Folder} is not one of the game's save folders.");
    }
}
