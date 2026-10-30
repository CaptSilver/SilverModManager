using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Library;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>
/// Matches each archive of a modlist to the library and says what is left to do about the ones it does not hold.
/// </summary>
public sealed class ArchiveInventory(
    ILibraryArchiveLocator locator,
    ILoginManager loginManager,
    IGameDomainToGameIdMappingCache mappingCache,
    IConnection connection,
    IFileStore fileStore,
    IFileIdentityResolver identityResolver,
    IFileHashesService fileHashes)
{
    /// <summary>The reader of the loadout's game files that <see cref="Build"/> checked the list against.</summary>
    public GameFileSourceLookup GameFilesFor(Loadout.ReadOnly loadout) => new(loadout, connection, fileStore, identityResolver, fileHashes);

    /// <summary>
    /// The reader of an installation's game files when there is no loadout to go through, such as one the user picked
    /// but has not created yet. Backups are pinned to the installation, so they still count.
    /// </summary>
    public GameFileSourceLookup GameFilesFor(GameInstallation installation)
    {
        // An installation the app has never managed has no metadata row, and so no backups: the game folder is all there is.
        var registry = connection.ServiceProvider.GetRequiredService<IGameRegistry>();
        EntityId? installationId = registry.TryGetMetadata(installation, out var metadata) ? metadata.Id : null;
        return new GameFileSourceLookup(installation, installationId, connection, fileStore, identityResolver, fileHashes);
    }

    public Task<ArchivePlan> Build(ModList list, IDb db, Loadout.ReadOnly loadout, CancellationToken cancellationToken = default)
        => Build(list, db, GameFilesFor(loadout), cancellationToken);

    /// <summary>
    /// Matches every archive of <paramref name="list"/>. The archives that only need a lookup go first, then the game
    /// files, which are read from disk and take the time; <paramref name="progress"/> follows both.
    /// </summary>
    public async Task<ArchivePlan> Build(ModList list, IDb db, GameFileSourceLookup gameFiles, CancellationToken cancellationToken = default, IProgress<PlanProgress>? progress = null)
    {
        var rows = new ArchivePlanRow[list.Archives.Count];
        var gate = new PlanProgressGate(progress);

        var lookups = Enumerable.Range(0, rows.Length).Where(i => list.Archives[i].State is not GameFileSourceState).ToArray();
        var done = 0;
        gate.Report(new PlanProgress(PlanPhase.CheckingLibrary, 0, lookups.Length));
        foreach (var index in lookups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows[index] = await Classify(list.Archives[index], db, gameFiles, cancellationToken);
            gate.Report(new PlanProgress(PlanPhase.CheckingLibrary, ++done, lookups.Length));
        }

        var reads = Enumerable.Range(0, rows.Length).Where(i => list.Archives[i].State is GameFileSourceState).ToArray();
        var totalBytes = reads.Sum(i => list.Archives[i].Size);
        long bytesBefore = 0;
        foreach (var index in reads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archive = list.Archives[index];
            var name = ((GameFileSourceState)archive.State).GameFile.ToString();
            long thisFile = 0;
            var before = bytesBefore;
            gate.Report(new PlanProgress(PlanPhase.HashingGameFiles, before, totalBytes, name));
            rows[index] = await ClassifyGameFile(archive, (GameFileSourceState)archive.State, gameFiles,
                read => gate.Report(new PlanProgress(PlanPhase.HashingGameFiles, before + Math.Min(thisFile += read, archive.Size), totalBytes, name)),
                cancellationToken);
            // A file read from the store's recorded hashes was not read at all; it still counts as done.
            bytesBefore += archive.Size;
            gate.Report(new PlanProgress(PlanPhase.HashingGameFiles, bytesBefore, totalBytes, name));
        }
        return new ArchivePlan(rows);
    }

    private async Task<ArchivePlanRow> Classify(ArchiveEntry archive, IDb db, GameFileSourceLookup gameFiles, CancellationToken cancellationToken)
    {
        // A game file is never in the library, whatever its hash says.
        if (archive.State is GameFileSourceState gameFile)
            return await ClassifyGameFile(archive, gameFile, gameFiles, null, cancellationToken);

        if (locator.FindByXxHash64(db, archive.XxHash64) is { } held)
            return new ArchivePlanRow(archive, ArchiveState.Have, null, null, held);

        if (archive.State is NexusState nexus)
        {
            var domain = NexusDomain(nexus);
            if (domain is { } known && FindByIdentity(db, known, nexus, archive) is { } identified)
                return new ArchivePlanRow(archive, ArchiveState.Have, null, null, identified);

            if (domain is null)
                return Row(archive, ArchiveState.Unsupported, $"the list's game '{nexus.GameName}' has no Nexus Mods domain here");

            var page = NexusModsUrlBuilder.GetFileDownloadUri(domain.Value, ModId.From((uint)nexus.ModId), FileId.From((uint)nexus.FileId), useNxmLink: true);
            return loginManager.IsPremium
                ? new ArchivePlanRow(archive, ArchiveState.Automatic, null, page, null)
                : new ArchivePlanRow(archive, ArchiveState.Browser, null, page, null);
        }

        return archive.State switch
        {
            HttpState { Headers.Length: > 0 } http => Row(archive, ArchiveState.Manual, "needs request headers", http.Url),
            HttpState => Row(archive, ArchiveState.Automatic),
            WabbajackCdnState => Row(archive, ArchiveState.Automatic),
            ManualState manual => Row(archive, ArchiveState.Manual, manual.Prompt, manual.Url),
            MegaState mega => Row(archive, ArchiveState.Manual, null, mega.Url),
            GoogleDriveState drive => Row(archive, ArchiveState.Manual, null, new Uri($"https://drive.google.com/uc?id={drive.Id}&export=download")),
            MediaFireState mediaFire => Row(archive, ArchiveState.Manual, null, mediaFire.Url),
            ModDbState modDb => Row(archive, ArchiveState.Manual, null, modDb.Url),
            LoversLabState loversLab => Row(archive, ArchiveState.Manual, null, loversLab.Url),
            VectorPlexusState vectorPlexus => Row(archive, ArchiveState.Manual, null, vectorPlexus.Url),
            UnsupportedState unsupported => Row(archive, ArchiveState.Unsupported, $"the list uses a source this app cannot download from ({unsupported.TypeName})", Uri.TryCreate(unsupported.Url, UriKind.Absolute, out var url) ? url : null),
            _ => Row(archive, ArchiveState.Unsupported, $"the list uses a source this app does not know ({archive.State.GetType().Name})"),
        };
    }

    private static ArchivePlanRow Row(ArchiveEntry archive, ArchiveState state, string? reason = null, Uri? page = null)
        => new(archive, state, reason, page, null);

    /// <summary>The domain the state's files are listed under, never the loadout's own: SSE lists pull from the Skyrim (LE) domain.</summary>
    private static GameDomain? NexusDomain(NexusState state) => WabbajackGames.NexusDomainFor(state.GameName);

    /// <summary>
    /// A library file that came from this very Nexus Mods file. File ids are immutable, so identity is as good as a
    /// hash for something the app downloaded itself, provided the size agrees. A recorded xxHash64 that differs rules it out.
    /// </summary>
    private LibraryFile.ReadOnly? FindByIdentity(IDb db, GameDomain domain, NexusState state, ArchiveEntry archive)
    {
        var gameId = mappingCache.GetId(domain);
        var uid = new FileUid(FileId.From((uint)state.FileId), gameId);

        foreach (var datom in db.Datoms(NexusModsFileMetadata.Uid, uid))
        {
            var metadata = NexusModsFileMetadata.Load(db, datom.E);
            if (!metadata.IsValid()) continue;

            foreach (var item in NexusModsLibraryItem.FindByFileMetadata(db, metadata))
            {
                var file = LibraryFile.Load(db, item.Id);
                if (!file.IsValid()) continue;

                if (file.XxHash64Identity() is { } known)
                {
                    if (known.Equals(archive.XxHash64)) return file;
                    continue;
                }

                if (NexusModsFileMetadata.Size.TryGetValue(metadata, out var recorded) && recorded.Value == (ulong)archive.Size)
                    return file;
            }
        }
        return null;
    }

    private static async Task<ArchivePlanRow> ClassifyGameFile(ArchiveEntry archive, GameFileSourceState state, GameFileSourceLookup gameFiles, Action<int>? onRead, CancellationToken cancellationToken)
    {
        var described = await gameFiles.Describe(state.GameFile, cancellationToken, onRead);
        if (described is null)
            return Row(archive, ArchiveState.GameFileVersion, $"'{state.GameFile}' is not in your game folder; the list was made for game version {state.GameVersion}");

        if (described.Value.XxHash64.Equals(state.XxHash64)) return Row(archive, ArchiveState.GameFile);

        var yours = gameFiles.InstalledVersion?.ToString() ?? "unknown";
        return Row(archive, ArchiveState.GameFileVersion,
            $"'{state.GameFile}' differs from the list's copy: the list was made for game version {state.GameVersion}, yours is {yours}");
    }
}
