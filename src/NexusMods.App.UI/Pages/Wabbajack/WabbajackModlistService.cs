using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Games.Modlists;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Settings;
using NexusMods.Wabbajack;
using NexusMods.Wabbajack.Acquire;
using NexusMods.Wabbajack.Format;
using NexusMods.Wabbajack.Install;
using NexusMods.Wabbajack.Mapping;
using NexusMods.Wabbajack.Models;
using Loadout = NexusMods.Sdk.Loadouts.Loadout;

namespace NexusMods.App.UI.Pages.Wabbajack;

/// <summary>The real <see cref="IWabbajackModlistService"/>: the library, the games registry and the install job.</summary>
[UsedImplicitly]
public sealed class WabbajackModlistService(IServiceProvider provider) : IWabbajackModlistService
{
    private const string DefaultBannerName = "modlist-image.png";

    private readonly IConnection _connection = provider.GetRequiredService<IConnection>();
    private readonly IFileStore _fileStore = provider.GetRequiredService<IFileStore>();
    private readonly ILogger<WabbajackModlistService> _logger = provider.GetRequiredService<ILogger<WabbajackModlistService>>();

    private readonly Lock _lock = new();
    private (LibraryFileId File, ModList List)? _lastList;
    private readonly Dictionary<LibraryFileId, Bitmap?> _banners = new();

    private WabbajackModlistLibraryFile.ReadOnly Modlist(LibraryFileId file)
        => WabbajackModlistLibraryFile.Load(_connection.Db, file.Value);

    private LibraryArchive.ReadOnly ArchiveOf(WabbajackModlistLibraryFile.ReadOnly modlist)
        => modlist.AsLibraryFile().TryGetAsLibraryArchive(out var archive)
            ? archive
            : throw new UnsupportedModlistException("The modlist file is not an expanded archive, so it cannot be read");

    private async Task<ModList> ReadList(LibraryFileId file, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_lastList is { } cached && cached.File == file) return cached.List;
        }

        var list = await ModlistArchive.ReadModlist(_fileStore, ArchiveOf(Modlist(file)), cancellationToken);
        lock (_lock) _lastList = (file, list);
        return list;
    }

    public async Task<WabbajackModlistDetails> Open(LibraryFileId file, CancellationToken cancellationToken)
    {
        var modlist = Modlist(file);
        var banner = await ReadBanner(file, modlist, cancellationToken);
        return new WabbajackModlistDetails(
            modlist.Name,
            modlist.Author,
            modlist.Version,
            modlist.GameType,
            WabbajackGames.GameIdFor(modlist.GameType),
            modlist.ArchiveCount,
            modlist.ArchiveSize,
            modlist.InstallSize,
            banner);
    }

    private async Task<Bitmap?> ReadBanner(LibraryFileId file, WabbajackModlistLibraryFile.ReadOnly modlist, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_banners.TryGetValue(file, out var cached)) return cached;
        }

        Bitmap? banner = null;
        try
        {
            var list = await ReadList(file, cancellationToken);
            var name = list.Image.Length > 0 ? list.Image : DefaultBannerName;
            await using var stream = await ModlistArchive.ReadBlob(_fileStore, ArchiveOf(modlist), name, cancellationToken);
            banner = new Bitmap(stream);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A list without a banner, or one the decoder cannot read, is still a list.
            _logger.LogDebug(e, "No banner for '{List}'", modlist.Name);
        }

        lock (_lock) _banners[file] = banner;
        return banner;
    }

    public async Task<ArchivePlan> BuildPlan(LibraryFileId file, LoadoutId target, CancellationToken cancellationToken)
    {
        var list = await ReadList(file, cancellationToken);
        var db = _connection.Db;
        return await provider.GetRequiredService<ArchiveInventory>().Build(list, db, Loadout.Load(db, target), cancellationToken);
    }

    public async Task<IReadOnlyList<RelativePath>> GameFilesReplaced(LibraryFileId file, LoadoutId target, CancellationToken cancellationToken)
    {
        var list = await ReadList(file, cancellationToken);
        var installation = Loadout.Load(_connection.Db, target).InstallationInstance;
        if (installation.Game is not IMo2InstanceTarget mo2) return [];

        var gameFolder = installation.Locations[LocationId.Game].Path;
        var tos = PathCasing.Unify(list.Directives.Select(d => d.To));
        var roots = GameRootFolders(installation, mo2, list.Directives.Select(d => tos[d.To]).ToArray());
        // Which profile is selected only matters for profile files, and those are never game files.
        var context = new Mo2Context("", roots, mo2.ProfileSettingsFiles(installation), relative => mo2.ModFilePath(installation, relative));

        var replaced = new Dictionary<string, RelativePath>(StringComparer.OrdinalIgnoreCase);
        foreach (var directive in list.Directives)
        {
            if (Mo2Layout.Classify(tos[directive.To], context) is not Mo2Target.GameRootFile root) continue;
            if (!gameFolder.Combine(root.Path).FileExists) continue;
            replaced.TryAdd(root.Path.ToString(), root.Path);
        }
        return replaced.Values.OrderBy(path => path.ToString(), StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlySet<string> GameRootFolders(GameInstallation installation, IMo2InstanceTarget target, IReadOnlyList<RelativePath> tos)
    {
        var folders = new Dictionary<string, HashSet<RelativePath>>(StringComparer.OrdinalIgnoreCase);
        foreach (var to in tos)
        {
            var parts = to.ToString().Split('/');
            if (parts.Length < 2) continue;

            if (!folders.TryGetValue(parts[0], out var files)) folders[parts[0]] = files = [];
            if (parts.Length == 2) files.Add(RelativePath.FromUnsanitizedInput(parts[1]));
        }

        return folders
            .Where(folder => target.IsGameRootFolder(installation, folder.Key, folder.Value))
            .Select(folder => folder.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<LoadoutId> CreateLoadout(GameId game, CancellationToken cancellationToken)
    {
        var installation = provider.GetRequiredService<IGameRegistry>().LocateGameInstallations().FirstOrDefault(i => i.Game.GameId == game)
            ?? throw new InvalidOperationException("The game is not installed, so there is nothing to create a loadout for");
        var loadout = await provider.GetRequiredService<ILoadoutManager>().CreateLoadout(installation);
        return loadout.LoadoutId;
    }

    public IWabbajackInstallRun StartInstall(LibraryFileId file, LoadoutId target, InstallOptions options)
    {
        var task = InstallModlistJob.Create(provider, target, Modlist(file), options);
        return new InstallRun(task.JobDefinition.Progress.Changes, Await(task));
    }

    private static async Task Await(IJobTask<InstallModlistJob, WabbajackModlistLoadoutGroup.ReadOnly> task) => _ = await task;

    private sealed class InstallRun(IObservable<ModlistInstallProgress.Snapshot> progress, Task completion) : IWabbajackInstallRun
    {
        public IObservable<ModlistInstallProgress.Snapshot> Progress => progress;
        public Task Completion => completion;
    }

    public Size? FreeSpace()
    {
        try
        {
            var fileSystem = provider.GetRequiredService<IFileSystem>();
            var location = provider.GetRequiredService<ISettingsManager>().Get<NexusMods.DataModel.DataModelSettings>().ArchiveLocations[0].ToPath(fileSystem);
            // The folder may not exist yet; its nearest existing parent is on the same filesystem.
            var probe = location;
            while (!probe.DirectoryExists() && probe.Parent != probe) probe = probe.Parent;
            return Size.FromLong(new DriveInfo(probe.ToString()).AvailableFreeSpace);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _logger.LogDebug(e, "Could not read the free space of the archive location");
            return null;
        }
    }

    public Task<IReadOnlyList<UnmatchedInbox>> CheckInbox(CancellationToken cancellationToken)
        => provider.GetRequiredService<ArchiveInbox>().Scan(cancellationToken);

    public void OpenInboxFolder()
        => provider.GetRequiredService<IOSInterop>().OpenDirectory(provider.GetRequiredService<WabbajackPaths>().Inbox);

    public async Task<IReadOnlyList<UnmatchedInbox>> AddToInbox(IReadOnlyList<AbsolutePath> files, CancellationToken cancellationToken)
    {
        var inbox = provider.GetRequiredService<WabbajackPaths>().Inbox;
        foreach (var source in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CopyIntoInbox(source, inbox, cancellationToken);
        }
        return await CheckInbox(cancellationToken);
    }

    /// <summary>
    /// Copies <paramref name="source"/> into the inbox under its own name. The copy goes to a <c>.part</c> file first,
    /// which the inbox scan skips, so its watcher never hashes half a file; and a file dropped from the inbox itself
    /// is read in full before anything replaces it, rather than truncated by opening its own path for writing.
    /// </summary>
    public static async Task CopyIntoInbox(AbsolutePath source, AbsolutePath inbox, CancellationToken cancellationToken)
    {
        var destination = inbox.Combine(source.FileName);
        var partial = inbox.Combine(source.FileName + ".part");
        try
        {
            await using (var input = source.Read())
            await using (var output = partial.Create())
            {
                await input.CopyToAsync(output, cancellationToken);
            }
            partial.FileSystem.MoveFile(partial, destination, overwrite: true);
        }
        finally
        {
            if (partial.FileExists) partial.Delete();
        }
    }
}
