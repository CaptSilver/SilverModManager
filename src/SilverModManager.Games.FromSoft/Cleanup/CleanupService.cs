using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Settings;
using SilverModManager.Sdk.Steam;

namespace SilverModManager.Games.FromSoft.Cleanup;

/// <param name="Done">The steps that ran, in order.</param>
/// <param name="Failure">Why the cleanup stopped, or null when every step ran.</param>
public sealed record CleanupResult(ImmutableArray<CleanupStep> Done, string? Failure)
{
    public bool Succeeded => Failure is null;
}

/// <summary>
/// Plans and applies the cleanup of one installation's <c>Game</c> folder. This is the only code that writes to
/// that folder, and only for a plan the user has seen and confirmed.
/// </summary>
public sealed class CleanupService(
    ISettingsManager settings,
    IFileSystem fileSystem,
    IFileHashesService fileHashes,
    IImmutableFlagProbe immutableProbe,
    ISteamLaunchOptionWriter launchOptions,
    IFileStore fileStore,
    IConnection connection,
    IGameLaunchSession launchSession,
    ILogger<CleanupService> logger)
{
    private const string LinksRecord = "links.json";

    private static readonly JsonSerializerOptions RecordOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record RemovedLink(string Name, string Target);

    /// <summary>What the folder is like now, entry by entry.</summary>
    public IReadOnlyList<InventoryEntry> Inventory(Loadout.ReadOnly loadout)
        => GameFolderInventory.Take(GameFolderOf(loadout));

    /// <summary>The steps the folder needs. Reads the disk, Steam's launch options and the game's file list; changes nothing.</summary>
    public CleanupPlan Plan(Loadout.ReadOnly loadout)
    {
        var installation = loadout.InstallationInstance;
        var game = GameOf(installation);
        var gameFolder = GameFolderOf(loadout);
        var launcherName = game.Cleanup.Launcher;
        var immutable = false;
        if (launcherName is not null)
        {
            var launcher = gameFolder / launcherName;
            immutable = launcher.FileExists && immutableProbe.IsImmutable(launcher);
        }

        return CleanupPlanner.Build(new CleanupInput(
            gameFolder.GetFullPath(),
            GameFolderInventory.Take(gameFolder),
            Baseline(loadout),
            immutable,
            LaunchOptionChange(installation, loadout),
            installation.LocatorResult.StoreUpdatePending), game.Cleanup, game.Layout);
    }

    /// <summary>
    /// Runs the steps of <paramref name="confirmed"/>, the plan the user was shown, after checking that the folder
    /// still asks for exactly those. Stops at the first step that fails; a later run plans what is left.
    /// </summary>
    public async Task<CleanupResult> ApplyAsync(Loadout.ReadOnly loadout, CleanupPlan confirmed, CancellationToken cancellationToken = default)
    {
        var installation = loadout.InstallationInstance;
        if (launchSession.IsRunning(installation))
            return Refused($"{installation.Game.DisplayName} is running or being launched, so its launch lock is held. Close it and try again.");

        var current = Plan(loadout);
        if (!current.SameSteps(confirmed))
            return Refused("The game folder has changed since the steps were shown. Plan again and confirm the new steps.");

        if (current.Steps.FirstOrDefault(s => s.Kind == CleanupStepKind.UserCommand) is { } command)
            return Refused($"{GameOf(loadout.InstallationInstance).Cleanup.Launcher} is still immutable, so nothing was changed. {command.Description}");

        if (current.Steps.FirstOrDefault(s => s.IsBlocked) is { } blocked)
            return Refused($"Nothing was changed: {blocked.BlockedReason}.");

        var gameFolder = GameFolderOf(loadout);
        try
        {
            // Both come before the first removal: the files are only in the game folder until they are in the
            // loadout, and the link targets are only known while the links exist.
            await AddConfigFiles(loadout, gameFolder, current.Steps.Where(s => s.Kind == CleanupStepKind.Copy), cancellationToken);
            await RecordLinks(installation, gameFolder, current.Steps.Where(s => s.Kind == CleanupStepKind.Delete));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogError(e, "Cleanup of {Folder} stopped before changing anything", gameFolder);
            return Refused($"Nothing was changed: {e.Message}");
        }

        var done = ImmutableArray.CreateBuilder<CleanupStep>();
        foreach (var step in current.Steps.Where(s => s.Acts))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (step.Kind != CleanupStepKind.Copy && Run(installation, loadout, gameFolder, step) is { } failure)
                return new CleanupResult(done.ToImmutable(), failure);

            logger.LogInformation("Cleanup step done: {Step}", step.Description);
            done.Add(step);
        }

        return new CleanupResult(done.ToImmutable(), null);
    }

    private static CleanupResult Refused(string reason) => new([], reason);

    private static IFromSoftGame GameOf(GameInstallation installation)
        => installation.Game as IFromSoftGame
           ?? throw new InvalidOperationException($"{installation.Game.DisplayName} is not a FromSoft game, so its game folder cannot be cleaned up.");

    private static AbsolutePath GameFolderOf(Loadout.ReadOnly loadout)
    {
        var gameFolder = FromSoftPaths.GameFolder(GameOf(loadout.InstallationInstance).Layout, loadout.InstallationInstance);
        if (!gameFolder.DirectoryExists())
            throw new InvalidOperationException($"The game folder {gameFolder} does not exist, so there is nothing to clean up.");

        return gameFolder;
    }

    /// <summary>The paths of the game's own files relative to the <c>Game</c> folder, or null when the build is not covered.</summary>
    private IReadOnlySet<string>? Baseline(Loadout.ReadOnly loadout)
    {
        var layout = GameOf(loadout.InstallationInstance).Layout;
        var ids = (loadout.InstallationInstance.LocatorResult.Store, loadout.LocatorIds.ToArray());
        if (!fileHashes.CoversInstalledBuild(ids)) return null;

        return fileHashes.GetGameFiles(ids)
            .Select(f => f.Path)
            .Where(p => FromSoftPaths.IsInGameFolder(layout, p))
            .Select(p => p.Path.Path[(layout.GameSubfolder.Length + 1)..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private LaunchOptionChange? LaunchOptionChange(GameInstallation installation, Loadout.ReadOnly loadout)
    {
        var located = installation.LocatorResult;
        if (located.Store != GameStore.Steam || located.LinuxCompatabilityDataProvider is not { } provider || !uint.TryParse(located.StoreIdentifier, out var appId))
            return null;

        var line = LocalConfigLaunchOptions.Read(provider.SteamLocalConfigPath, appId).Line;
        return line is not null && SteamLaunchOptionLine.FindOtherManager(line) is not null
            ? new LaunchOptionChange(line, launchOptions.ComposeLine(loadout))
            : null;
    }

    /// <summary>Puts each copied file into the loadout's overrides at its path inside the mods folder, so the overlay places it over the game folder.</summary>
    private async Task AddConfigFiles(Loadout.ReadOnly loadout, AbsolutePath gameFolder, IEnumerable<CleanupStep> copies, CancellationToken cancellationToken)
    {
        var files = new List<(GamePath Path, Hash Hash, Size Size, AbsolutePath Source)>();
        foreach (var copy in copies)
        {
            var source = gameFolder / copy.RequiredSource;
            var bytes = await File.ReadAllBytesAsync(source.GetFullPath(), cancellationToken);
            files.Add((new GamePath(LocationId.Me3Mods, copy.RequiredTarget), bytes.xxHash3(), Size.FromLong(bytes.Length), source));
        }

        if (files.Count == 0) return;

        var missing = new List<ArchivedFileEntry>();
        foreach (var file in files.DistinctBy(f => f.Hash))
        {
            if (!await fileStore.HaveFile(file.Hash)) missing.Add(new ArchivedFileEntry(new NativeFileStreamFactory(file.Source), file.Hash, file.Size));
        }

        await fileStore.BackupFiles(missing, deduplicate: false, cancellationToken);

        // The loadout is read again so a second run does not add a row beside the one the first run made.
        var fresh = Loadout.Load(connection.Db, loadout.LoadoutId);
        var synchronizer = (ALoadoutSynchronizer)fresh.InstallationInstance.GetGame().Synchronizer;
        using var tx = connection.BeginTransaction();
        var overrides = synchronizer.GetOrCreateOverridesGroup(tx, fresh);
        foreach (var file in files) OverridesGroupItems.UpsertFile(tx, fresh, overrides, file.Path, file.Hash, file.Size);
        await tx.Commit();
    }

    /// <summary>
    /// Writes the name and target of every link a step is about to remove, merged with what earlier runs recorded,
    /// so the graphics hook can be set up again through the overlay once it is known how.
    /// </summary>
    private async Task RecordLinks(GameInstallation installation, AbsolutePath gameFolder, IEnumerable<CleanupStep> deletes)
    {
        var inventory = GameFolderInventory.Take(gameFolder).ToDictionary(e => e.Path, StringComparer.Ordinal);
        var links = deletes
            .Select(d => inventory.GetValueOrDefault(d.RequiredSource))
            .OfType<InventoryEntry>()
            .Where(e => e.Kind == InventoryKind.Link)
            .Select(e => new RemovedLink(e.Path, e.LinkTarget ?? throw new InvalidOperationException($"The link {e.Path} was listed without its target.")))
            .ToArray();
        if (links.Length == 0) return;

        var layout = GameOf(installation).Layout;
        var folder = FromSoftPaths.CleanupFolder(layout, settings.Get<FromSoftSettings>(), fileSystem, FromSoftPaths.InstallationFolderName(layout, installation.LocatorResult));
        var record = folder / LinksRecord;

        var all = new List<RemovedLink>();
        if (record.FileExists) all.AddRange(JsonSerializer.Deserialize<List<RemovedLink>>(await record.ReadAllTextAsync(), RecordOptions) ?? []);
        all.RemoveAll(old => links.Any(l => l.Name == old.Name));
        all.AddRange(links);

        folder.CreateDirectory();
        await File.WriteAllTextAsync(record.GetFullPath(), JsonSerializer.Serialize(all.OrderBy(l => l.Name, StringComparer.Ordinal), RecordOptions));
    }

    /// <summary>Runs one step that changes the folder or Steam's config. Returns why it failed, or null.</summary>
    private string? Run(GameInstallation installation, Loadout.ReadOnly loadout, AbsolutePath gameFolder, CleanupStep step)
    {
        try
        {
            switch (step.Kind)
            {
                case CleanupStepKind.Move:
                    File.Move((gameFolder / step.RequiredSource).GetFullPath(), (gameFolder / step.RequiredTarget).GetFullPath(), overwrite: true);
                    return null;
                case CleanupStepKind.Delete:
                    Remove((gameFolder / step.RequiredSource).GetFullPath());
                    return null;
                case CleanupStepKind.ReplaceLaunchOption:
                    var result = launchOptions.Replace(installation, loadout, step.RequiredSource);
                    return result.IsRefused ? result.Refusal : null;
                default:
                    throw new InvalidOperationException($"A {step.Kind} step does not change the game folder.");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogError(e, "Cleanup step failed: {Step}", step.Description);
            return $"{step.Description} failed: {e.Message}";
        }
    }

    /// <summary>Removes a link, a file or a folder. A link is unlinked without looking at what it points to.</summary>
    private static void Remove(string path)
    {
        if (new FileInfo(path).LinkTarget is not null) new FileInfo(path).Delete();
        else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else File.Delete(path);
    }
}
