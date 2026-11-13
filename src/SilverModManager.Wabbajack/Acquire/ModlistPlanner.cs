using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>Works out what a modlist still needs from the library; the modlist page and the command line both ask it.</summary>
public interface IModlistPlanner
{
    /// <summary>
    /// Matches the list's archives against the library and the game folder. <paramref name="target"/> is the loadout
    /// the list will install into, or null for one that does not exist yet.
    /// </summary>
    Task<PlannedModlist> BuildPlan(LibraryFileId file, LoadoutId? target, IProgress<PlanProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// The test of which held archives are complete for the list (see <see cref="HeldArchiveCheck"/>), without a
    /// plan: for a page that counts held archives but checks nothing against the game folder. The list is read once.
    /// </summary>
    Task<HeldArchiveCheck> ReadHeldCheck(LibraryFileId file, CancellationToken cancellationToken);
}

/// <summary>
/// What a list needs (<paramref name="Plan"/>) and which archive each of its mod folders is mostly built from, so a page can
/// show a folder as downloading while its archive is. <paramref name="MainArchiveByFolder"/> and <paramref name="Folders"/>
/// are null when there is no loadout to lay the list out for, or the installer refuses the list.
/// </summary>
public sealed record PlannedModlist(ArchivePlan Plan, IReadOnlyDictionary<string, FileIdentity>? MainArchiveByFolder, IReadOnlyList<ModFolderPlan>? Folders);

/// <summary>The list's game is not installed, so there is no game folder to check its files against.</summary>
public sealed class ModlistGameMissingException(string message) : UserFacingException(message)
{
    public override string Title => "Game not installed";
}

public sealed class ModlistPlanner(IServiceProvider provider) : IModlistPlanner
{
    private readonly IConnection _connection = provider.GetRequiredService<IConnection>();
    private readonly IFileStore _fileStore = provider.GetRequiredService<IFileStore>();

    private readonly Lock _lock = new();
    private (LibraryFileId File, ModList List)? _lastList;
    // Small next to the list, and a library file never changes, so every list that was asked about keeps its check.
    private readonly Dictionary<LibraryFileId, HeldArchiveCheck> _checks = [];

    private WabbajackModlistLibraryFile.ReadOnly Modlist(LibraryFileId file)
        => WabbajackModlistLibraryFile.Load(_connection.Db, file.Value);

    private LibraryArchive.ReadOnly ArchiveOf(WabbajackModlistLibraryFile.ReadOnly modlist)
        => modlist.AsLibraryFile().TryGetAsLibraryArchive(out var archive)
            ? archive
            : throw new UnsupportedModlistException("The modlist file is not an expanded archive, so it cannot be read");

    /// <summary>The parsed list. The last one read is kept, so the page's several questions about a list parse it once.</summary>
    public async Task<ModList> ReadList(LibraryFileId file, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_lastList is { } cached && cached.File == file) return cached.List;
        }

        var list = await ModlistArchive.ReadModlist(_fileStore, ArchiveOf(Modlist(file)), cancellationToken);
        lock (_lock) _lastList = (file, list);
        return list;
    }

    public async Task<HeldArchiveCheck> ReadHeldCheck(LibraryFileId file, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_checks.TryGetValue(file, out var known)) return known;
        }

        var check = HeldArchiveCheck.For(await ReadList(file, cancellationToken), _fileStore);
        lock (_lock) return _checks.TryAdd(file, check) ? check : _checks[file];
    }

    public async Task<PlannedModlist> BuildPlan(LibraryFileId file, LoadoutId? target, IProgress<PlanProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new PlanProgress(PlanPhase.ReadingList));
        var list = await ReadList(file, cancellationToken);
        var db = _connection.Db;
        var inventory = provider.GetRequiredService<ArchiveInventory>();
        var gameFiles = target is { } loadoutId
            ? inventory.GameFilesFor(Loadout.Load(db, loadoutId))
            // The loadout does not exist yet; it will be created on this installation.
            : inventory.GameFilesFor(InstallationFor(file));
        StartWatchingForDownloads();
        var plan = (await inventory.Build(list, db, gameFiles, cancellationToken, progress)) with { Modlist = file };
        if (target is not { } loadout) return new PlannedModlist(plan, null, null);
        return await WithFolders(plan, loadout, list, file, cancellationToken);
    }

    // The folder map is for the page's rows only; a list the installer refuses is still planned and downloaded.
    private async Task<PlannedModlist> WithFolders(ArchivePlan plan, LoadoutId target, ModList list, LibraryFileId file, CancellationToken cancellationToken)
    {
        try
        {
            var classified = await InstallModlistJob.ClassifyList(target, list, ArchiveOf(Modlist(file)), _connection, _fileStore, cancellationToken);
            var executable = InstallModlistJob.Executable(classified.Placements);
            return new PlannedModlist(
                plan,
                ModGroupArchives.MainArchiveByFolder(classified.Placements, classified.BsaInputs, list.Archives),
                InstallModlistJob.FolderPlans(executable, executable));
        }
        catch (UnsupportedModlistException e)
        {
            provider.GetService<ILogger<ModlistPlanner>>()?.LogDebug(e, "No mod rows for '{List}': {Reason}", list.Name, e.Message);
            return new PlannedModlist(plan, null, null);
        }
    }

    // The first point a plan can exist. Both calls do nothing until a row waits for the user, and repeat calls change nothing.
    private void StartWatchingForDownloads()
    {
        provider.GetRequiredService<ArchiveInbox>().WatchOnceArchivesAreWaiting();
        provider.GetRequiredService<DownloadsPickup>().Follow();
    }

    /// <summary>The installation of the list's game.</summary>
    /// <exception cref="ModlistGameMissingException">The game is not installed.</exception>
    public GameInstallation InstallationFor(LibraryFileId file)
    {
        var game = WabbajackGames.GameIdFor(Modlist(file).GameType)
            ?? throw new ModlistGameMissingException($"The app has no title for the game {MarkdownText.Code(Modlist(file).GameType)} of this modlist");
        return provider.GetRequiredService<IGameRegistry>().LocateGameInstallations().FirstOrDefault(i => i.Game.GameId == game)
            ?? throw new ModlistGameMissingException($"The modlist's game ({MarkdownText.Code(Modlist(file).GameType)}) is not installed, so its files cannot be checked");
    }
}
