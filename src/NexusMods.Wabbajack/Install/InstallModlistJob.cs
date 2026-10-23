using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Games.Modlists;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers.Conflicts;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using NexusMods.Platform.Proton.ToolRunner;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.Wabbajack.Acquire;
using NexusMods.Wabbajack.Format;
using NexusMods.Wabbajack.Mapping;
using NexusMods.Wabbajack.Models;
using PatchException = NexusMods.Wabbajack.Transforms.PatchException;
using UnsupportedTextureFormatException = NexusMods.Wabbajack.Transforms.UnsupportedTextureFormatException;

namespace NexusMods.Wabbajack.Install;

/// <summary>
/// Installs a modlist's archive members and inline files into a loadout as mod groups under a
/// <see cref="WabbajackModlistLoadoutGroup"/>. A directive the app cannot execute leaves a
/// <see cref="WabbajackSkippedFile"/> row. Once the files are written the groups are put in the order the profile's
/// modlist.txt gives, the profile's plugin list is staged, and the list is enabled unless something was skipped.
/// Running the job again on the same loadout continues in the same group and brings it in line with the list: files the
/// list still installs are kept, ones whose content changed or that the list no longer installs are removed.
/// </summary>
public sealed class InstallModlistJob : IJobDefinitionWithStart<InstallModlistJob, WabbajackModlistLoadoutGroup.ReadOnly>
{
    private const int TransactionSize = 2000;

    // The compiler records the hash of these before MO2 rewrites them, so the installed copy never matches.
    private static readonly string[] HashUnchecked = ["modlist.txt", "SkyrimPrefs.ini"];

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
    public required LoadoutId TargetLoadout { get; init; }
    public required WabbajackModlistLibraryFile.ReadOnly Modlist { get; init; }
    public required InstallOptions Options { get; init; }

    public required IConnection Connection { get; init; }
    public required IFileStore FileStore { get; init; }
    public required ArchiveInventory Inventory { get; init; }
    public required IDirectiveTransforms Transforms { get; init; }
    public required IPrefixResolver PrefixResolver { get; init; }
    public required WabbajackPaths Paths { get; init; }
    public required GeneratedFileWriter Writer { get; init; }
    public required IReadOnlyList<INestedArchiveOpener> Openers { get; init; }
    public required ILogger Logger { get; init; }
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member

    /// <summary>
    /// The toolbar entries of the list's ModOrganizer.ini that were not registered as tools, each with the reason, once the
    /// install has run. Entries left out on purpose (Mod Organizer itself, the game and its launcher) are not listed.
    /// </summary>
    public IReadOnlyList<string> UnregisteredTools { get; private set; } = [];

    /// <summary>How far the install is. Observe <see cref="ModlistInstallProgress.Changes"/> to follow it live.</summary>
    public ModlistInstallProgress Progress { get; } = new();

    public static IJobTask<InstallModlistJob, WabbajackModlistLoadoutGroup.ReadOnly> Create(
        IServiceProvider provider,
        LoadoutId target,
        WabbajackModlistLibraryFile.ReadOnly modlist,
        InstallOptions options)
    {
        var fileStore = provider.GetRequiredService<IFileStore>();
        var job = new InstallModlistJob
        {
            TargetLoadout = target,
            Modlist = modlist,
            Options = options,
            Connection = provider.GetRequiredService<IConnection>(),
            FileStore = fileStore,
            Inventory = provider.GetRequiredService<ArchiveInventory>(),
            Transforms = provider.GetRequiredService<IDirectiveTransforms>(),
            PrefixResolver = provider.GetRequiredService<IPrefixResolver>(),
            Paths = provider.GetRequiredService<WabbajackPaths>(),
            Writer = new GeneratedFileWriter(fileStore, provider.GetRequiredService<TemporaryFileManager>()),
            Openers = provider.GetServices<INestedArchiveOpener>().ToArray(),
            Logger = provider.GetRequiredService<ILogger<InstallModlistJob>>(),
        };
        return provider.GetRequiredService<IJobMonitor>().Begin<InstallModlistJob, WabbajackModlistLoadoutGroup.ReadOnly>(job);
    }

    public async ValueTask<WabbajackModlistLoadoutGroup.ReadOnly> StartAsync(IJobContext<InstallModlistJob> context)
    {
        var cancellationToken = context.CancellationToken;

        var check = await Check(cancellationToken);
        var (group, extrasFolder) = await EnsureGroup(check, cancellationToken);

        try
        {
            var prepared = await Resolve(check, cancellationToken);
            await Write(check, group, extrasFolder, prepared, cancellationToken);
            await RegisterTools(check, extrasFolder, cancellationToken);
            await Order(check, group, cancellationToken);
        }
        finally
        {
            // Releases the pins on the bytes the resolver backed up; the loadout files now reference them.
            _resolver?.Dispose();
        }

        return WabbajackModlistLoadoutGroup.Load(Connection.Db, group.Value);
    }

    #region Check

    /// <summary>One directive with the place it lands in the loadout, decided once for the whole job.</summary>
    private sealed record Placement(int Index, Directive Directive, RelativePath To, Mo2Target Target, string? Group, GamePath? Path);

    private sealed record Checked(
        Loadout.ReadOnly Loadout,
        ModList List,
        LibraryArchive.ReadOnly ListArchive,
        ArchivePlan Plan,
        GameFileSourceLookup GameFiles,
        IReadOnlyList<Placement> Placements,
        // TempId and path inside it, folded, to the directive that supplies the file.
        IReadOnlyDictionary<string, Dictionary<string, Placement>> BsaInputs);

    private async Task<Checked> Check(CancellationToken cancellationToken)
    {
        Progress.Enter(InstallPhase.Check, 0, 0);

        var loadout = Loadout.Load(Connection.Db, TargetLoadout);
        var installation = loadout.InstallationInstance;
        var target = RequireTarget(installation);
        _ = RequirePluginVariety(installation);

        if (!Modlist.AsLibraryFile().TryGetAsLibraryArchive(out var listArchive))
            throw new UnsupportedModlistException("The modlist file is not an expanded archive, so it cannot be read");
        var list = await ModlistArchive.ReadModlist(FileStore, listArchive, cancellationToken);
        RequireGame(installation, list);

        var unified = PathCasing.Unify(list.Directives.Select(d => d.To));
        var tos = list.Directives.Select(d => unified[d.To]).ToArray();

        var context = new Mo2Context(
            await SelectedProfile(list, tos, listArchive, cancellationToken),
            GameRootFolders(installation, target, tos),
            target.ProfileSettingsFiles(installation),
            relative => target.ModFilePath(installation, relative));

        var placements = Classify(list, tos, context);
        var bsaInputs = CollectBsaInputs(placements);

        var plan = await Inventory.Build(list, Connection.Db, loadout, cancellationToken);
        var missing = plan.Rows.Where(r => r.State is not (ArchiveState.Have or ArchiveState.GameFile)).ToArray();
        if (missing.Length > 0 && !Options.SkipMissingArchives) throw new ArchivesMissingException(missing);

        var (estimate, isLowerBound) = EstimateExtractedSize(plan);
        Progress.SetEstimate(estimate, isLowerBound);
        CountPathsNeverInstalled(placements, list);

        return new Checked(loadout, list, listArchive, plan, Inventory.GameFilesFor(loadout), placements, bsaInputs);
    }

    private static IMo2InstanceTarget RequireTarget(GameInstallation installation)
    {
        var name = installation.Game.DisplayName;
        if (installation.Game is not IMo2InstanceTarget target)
            throw new UnsupportedModlistException($"Wabbajack lists for {name} are not supported yet: the game cannot lay out a Mod Organizer instance");
        if (target.UnsupportedReason(installation) is { } reason)
            throw new UnsupportedModlistException($"Wabbajack lists for {name} are not supported yet: {reason}");
        return target;
    }

    /// <summary>The game's one load order, which the profile's plugin list is staged on.</summary>
    private static ISortOrderVariety RequirePluginVariety(GameInstallation installation)
    {
        var varieties = installation.GetGame().SortOrderManager.GetSortOrderVarieties();
        if (varieties.Count != 1)
            throw new UnsupportedModlistException($"Wabbajack lists for {installation.Game.DisplayName} are not supported yet: the game has {varieties.Count} load orders and a profile carries one plugin list");
        return varieties[0];
    }

    private static void RequireGame(GameInstallation installation, ModList list)
    {
        var listGame = WabbajackGames.GameIdFor(list.GameType)
            ?? throw new UnsupportedModlistException($"Wabbajack lists for {list.GameType} are not supported yet: this app has no title for that game");
        if (listGame != installation.Game.GameId)
            throw new UnsupportedModlistException($"The list is for {list.GameType}, but this loadout is for {installation.Game.DisplayName}");
    }

    private async Task<string> SelectedProfile(ModList list, IReadOnlyList<RelativePath> tos, LibraryArchive.ReadOnly listArchive, CancellationToken cancellationToken)
    {
        for (var i = 0; i < list.Directives.Count; i++)
        {
            if (list.Directives[i] is not InlineFile ini || !tos[i].ToString().Equals("ModOrganizer.ini", StringComparison.OrdinalIgnoreCase)) continue;

            await using var stream = await ModlistArchive.ReadBlob(FileStore, listArchive, ini.SourceDataId, cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            if (Mo2Ini.SelectedProfile(Mo2Ini.Parse(await reader.ReadToEndAsync(cancellationToken))) is { Length: > 0 } selected)
                return selected;
        }

        // No selection recorded: the only profile, or the first by name.
        return tos.Select(t => t.ToString().Split('/'))
            .Where(parts => parts.Length >= 3 && parts[0].Equals("profiles", StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[1])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? "";
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

    private static IReadOnlyList<Placement> Classify(ModList list, IReadOnlyList<RelativePath> tos, Mo2Context context)
    {
        var placements = new List<Placement>(list.Directives.Count);
        for (var i = 0; i < list.Directives.Count; i++)
        {
            var target = Mo2Layout.Classify(tos[i], context);
            var (group, path) = target switch
            {
                Mo2Target.ModFile mod => (mod.GroupName, (GamePath?)mod.Path),
                Mo2Target.ProfileSetting setting => (Mo2Layout.ProfileSettingsGroup, setting.Path),
                Mo2Target.GameRootFile root => (root.GroupName, new GamePath(LocationId.Game, root.Path)),
                _ => ((string?)null, (GamePath?)null),
            };
            placements.Add(new Placement(i, list.Directives[i], tos[i], target, group, path));
        }

        // One spelling per path inside each location, across all mods: two mods may write SKSE/ and skse/.
        var located = new List<(int Index, GamePath Path)>();
        foreach (var placement in placements)
        {
            if (placement.Path is { } path) located.Add((placement.Index, path));
        }

        var unified = new Dictionary<int, GamePath>();
        foreach (var byLocation in located.GroupBy(l => l.Path.LocationId))
        {
            var spelling = PathCasing.Unify(byLocation.Select(l => l.Path.Path));
            foreach (var (index, path) in byLocation) unified[index] = new GamePath(byLocation.Key, spelling[path.Path]);
        }

        return placements.Select(p => unified.TryGetValue(p.Index, out var path) ? p with { Path = path } : p).ToArray();
    }

    private static Dictionary<string, Dictionary<string, Placement>> CollectBsaInputs(IReadOnlyList<Placement> placements)
    {
        var inputs = new Dictionary<string, Dictionary<string, Placement>>();
        foreach (var placement in placements)
        {
            if (placement.Target is not Mo2Target.BsaInput input) continue;
            if (!inputs.TryGetValue(Fold(input.TempId), out var files)) inputs[Fold(input.TempId)] = files = new();
            files[Fold(input.Path)] = placement;
        }
        return inputs;
    }

    /// <summary>
    /// The sum of what the library holds for each archive, since the library keeps every extracted member and not the
    /// download. An archive it does not hold yet counts by its download size, which is the least it can come to.
    /// </summary>
    private static (Size Estimate, bool IsLowerBound) EstimateExtractedSize(ArchivePlan plan)
    {
        ulong total = 0;
        var isLowerBound = false;
        foreach (var row in plan.Rows)
        {
            switch (row.State)
            {
                case ArchiveState.Have:
                    var library = row.Library ?? throw new InvalidOperationException($"The plan has '{row.Archive.Name}' in the library but names no library file for it");
                    total += library.TryGetAsLibraryArchive(out var archive)
                        ? archive.Children.Aggregate(0UL, (sum, entry) => sum + entry.AsLibraryFile().Size.Value)
                        : library.Size.Value;
                    break;
                case ArchiveState.GameFile or ArchiveState.GameFileVersion:
                    break;
                default:
                    total += (ulong)row.Archive.Size;
                    isLowerBound = true;
                    break;
            }
        }
        return (Size.From(total), isLowerBound);
    }

    private void CountPathsNeverInstalled(IReadOnlyList<Placement> placements, ModList list)
    {
        Progress.AddDropped(placements.Count(p => p.Target is Mo2Target.Seed or Mo2Target.Dropped));
        Progress.AddExtras(placements.Count(p => p.Target is Mo2Target.Extra));

        // Files for an archive no CreateBsa directive builds go nowhere either.
        var built = list.Directives.OfType<CreateBsa>().Select(b => Fold(b.TempId)).ToHashSet();
        Progress.AddDropped(placements.Count(p => p.Target is Mo2Target.BsaInput input && !built.Contains(Fold(input.TempId))));
    }

    #endregion

    #region Group

    /// <summary>The group of this list in this loadout, created disabled on the first run. Rows of an earlier run's skips are cleared.</summary>
    private async Task<(LoadoutItemGroupId Group, AbsolutePath Extras)> EnsureGroup(Checked check, CancellationToken cancellationToken)
    {
        WabbajackModlistLoadoutGroup.ReadOnly? existing = null;
        foreach (var candidate in WabbajackModlistLoadoutGroup.FindByModlist(Connection.Db, Modlist))
        {
            if (candidate.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId != TargetLoadout) continue;
            existing = candidate;
            break;
        }

        using var tx = Connection.BeginTransaction();
        if (existing is { } found)
        {
            foreach (var skipped in WabbajackSkippedFile.FindByGroup(Connection.Db, found.Id))
                tx.Delete(skipped.Id, recursive: false);
            // A group made before folders carried the group id keeps the folder its tools were registered from.
            var folder = found.ExtrasFolder is { HasValue: true } recorded && recorded.Value.Length > 0
                ? FileSystem.Shared.FromUnsanitizedFullPath(recorded.Value)
                : Paths.ExtrasFor(check.List.Name, found.Id);
            folder.CreateDirectory();
            tx.Add(found.Id, WabbajackModlistLoadoutGroup.ExtrasFolder, folder.ToString());
            await tx.Commit();
            return (found.AsCollectionGroup().AsLoadoutItemGroup().LoadoutItemGroupId, folder);
        }

        var group = new WabbajackModlistLoadoutGroup.New(tx, out var id)
        {
            ModlistId = Modlist,
            CollectionGroup = new CollectionGroup.New(tx, id)
            {
                IsReadOnly = false,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, id)
                    {
                        Name = check.List.Name,
                        LoadoutId = TargetLoadout,
                        IsDisabled = true,
                    },
                },
            },
        };
        var result = await tx.Commit();
        var created = result.Remap(group);

        // The folder name needs the group's id, so it is recorded once the group exists. It goes in before anything is
        // written, so that removing the group finds the folder even if the install never got as far as writing a tool.
        var extras = Paths.ExtrasFor(check.List.Name, created.Id);
        using var record = Connection.BeginTransaction();
        record.Add(created.Id, WabbajackModlistLoadoutGroup.ExtrasFolder, extras.ToString());
        await record.Commit();
        return (created.AsCollectionGroup().AsLoadoutItemGroup().LoadoutItemGroupId, extras);
    }

    #endregion

    #region Resolve

    private sealed record Skip(SkipKind Kind, string Reason);

    private sealed record Prepared(Placement Placement, ResolvedMember? Member, Skip? Skip, bool Silent);

    private sealed class DirectiveSkipped(SkipKind kind, string reason) : Exception(reason)
    {
        public SkipKind Kind { get; } = kind;
    }

    private ArchivePathResolver NewResolver(Checked check)
        => new(check.Plan, Connection.Db, FileStore, check.GameFiles, Openers);

    private ArchivePathResolver? _resolver;

    private ArchivePathResolver Resolver => _resolver ?? throw new InvalidOperationException("Archive members are read before the resolve phase created the resolver");

    private async Task<IReadOnlyList<Prepared>> Resolve(Checked check, CancellationToken cancellationToken)
    {
        _resolver = NewResolver(check);
        var executable = check.Placements
            .Where(p => p.Target is Mo2Target.ModFile or Mo2Target.ProfileSetting or Mo2Target.GameRootFile or Mo2Target.Extra)
            .ToArray();
        Progress.Enter(InstallPhase.Resolve, executable.Length, executable.Sum(p => p.Directive.Size));

        var rows = check.Plan.Rows.GroupBy(r => r.Archive.XxHash64).ToDictionary(g => g.Key, g => g.First());
        var prepared = new List<Prepared>(executable.Length);
        foreach (var placement in executable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            prepared.Add(await Prepare(check, rows, placement, cancellationToken));
            Progress.Advance(1, placement.Directive.Size);
        }

        // Later kinds read what earlier ones wrote; within a kind the list's order is kept.
        return prepared.OrderBy(p => WriteOrder.Of(p.Placement.Directive)).ThenBy(p => p.Placement.Index).ToArray();
    }

    private async Task<Prepared> Prepare(Checked check, Dictionary<Hash, ArchivePlanRow> rows, Placement placement, CancellationToken cancellationToken)
    {
        if (placement.Target is Mo2Target.GameRootFile root)
        {
            if (Options.GameFilesNotToReplace.Any(kept => SameFile(kept, root.Path)))
                return new Prepared(placement, null, null, Silent: true);

            if (placement.Directive is FromArchive { Source.Parts.Count: 0 } vanilla
                && rows.TryGetValue(vanilla.Source.ArchiveXxHash64, out var gameRow)
                && gameRow.Archive.State is GameFileSourceState gameFile
                && SameFile(gameFile.GameFile, root.Path))
            {
                return await PrepareVanillaFile(check, placement, vanilla, gameRow, gameFile, cancellationToken);
            }
        }

        if (placement.Directive is not FromArchive from) return new Prepared(placement, null, null, Silent: false);

        try
        {
            return new Prepared(placement, await ResolveMember(rows, from, cancellationToken), null, Silent: false);
        }
        catch (DirectiveSkipped skipped)
        {
            return new Prepared(placement, null, new Skip(skipped.Kind, skipped.Message), Silent: false);
        }
    }

    /// <summary>A file the list copies from the game itself: nothing to write when the user's copy is the one the list wants.</summary>
    private static async Task<Prepared> PrepareVanillaFile(Checked check, Placement placement, FromArchive directive, ArchivePlanRow row, GameFileSourceState gameFile, CancellationToken cancellationToken)
    {
        var described = await check.GameFiles.Describe(gameFile.GameFile, cancellationToken);
        if (described is { } mine && mine.XxHash64 == directive.XxHash64)
            return new Prepared(placement, null, null, Silent: true);

        var reason = row.Reason ?? $"'{gameFile.GameFile}' differs from the list's copy (game version {gameFile.GameVersion})";
        return new Prepared(placement, null, new Skip(SkipKind.GameFileVersion, reason), Silent: false);
    }

    /// <summary>The member a FromArchive-family directive reads, or <see cref="DirectiveSkipped"/> saying why there is none.</summary>
    private async Task<ResolvedMember> ResolveMember(Dictionary<Hash, ArchivePlanRow> rows, FromArchive directive, CancellationToken cancellationToken)
    {
        if (!rows.TryGetValue(directive.Source.ArchiveXxHash64, out var row))
            throw new DirectiveSkipped(SkipKind.MemberNotFound, $"the list has no archive with hash {WabbajackHash.ToBase64(directive.Source.ArchiveXxHash64)}");

        switch (row.State)
        {
            case ArchiveState.Have or ArchiveState.GameFile:
                break;
            case ArchiveState.GameFileVersion:
                throw new DirectiveSkipped(SkipKind.GameFileVersion, row.Reason ?? $"'{row.Archive.Name}' is not the version the list was made for");
            case ArchiveState.Unsupported:
                throw new DirectiveSkipped(SkipKind.UnsupportedSource, row.Reason ?? $"'{row.Archive.Name}' comes from a source this app cannot fetch from");
            default:
                throw new DirectiveSkipped(SkipKind.MissingArchive, $"'{row.Archive.Name}' is not in the library{(row.Reason is { } why ? $" ({why})" : "")}");
        }

        ResolvedMember member;
        try
        {
            member = await Resolver.Resolve(directive.Source, cancellationToken);
        }
        catch (MemberNotFoundException e)
        {
            throw new DirectiveSkipped(SkipKind.MemberNotFound, e.Message);
        }

        // A plain copy has to be the very file the list recorded, and a patch the very file it was cut against.
        var expected = directive switch
        {
            PatchedFromArchive patched => patched.FromHash,
            _ when directive.GetType() == typeof(FromArchive) => directive.XxHash64,
            _ => (Hash?)null,
        };
        if (expected is null) return member;
        if (member.XxHash64 != expected)
            throw new DirectiveSkipped(SkipKind.HashMismatch, $"'{(directive.Source.Parts.Count > 0 ? directive.Source.Parts[^1].ToString() : row.Archive.Name)}' in '{row.Archive.Name}' is not the file the list was made with");
        return member;
    }

    private static bool SameFile(RelativePath a, RelativePath b)
        => string.Equals(Fold(a), Fold(b), StringComparison.Ordinal);

    #endregion

    #region Write

    private sealed class WriteState
    {
        /// <summary>Directive destinations, folded, to the file store hash and xxHash64 of what was written, for merges to read back and check.</summary>
        public Dictionary<string, (Hash Store, Hash XxHash64)> Written { get; } = new();

        /// <summary>Textures of the batch being written that are already being re-encoded, taken by <see cref="Produce"/> when it reaches them.</summary>
        public Dictionary<Prepared, Task<GeneratedFile>> Textures { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>Generated files waiting for the next batch to the file store.</summary>
        public List<GeneratedFile> Pending { get; } = new();

        /// <summary>Mod group name to its id: real once committed, a temp id while its transaction is open.</summary>
        public Dictionary<string, LoadoutItemGroupId> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> NewGroups { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per group, the files an earlier run (or this one) already created, as path and hash.</summary>
        public Dictionary<string, Dictionary<(GamePath Path, Hash Hash), KnownFile>> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A file row of a group. <see cref="Ids"/> is empty for one made in this run, and <see cref="Wanted"/> is set once this run's list asks for it.</summary>
    private sealed class KnownFile
    {
        public List<EntityId> Ids { get; } = [];
        public bool Wanted { get; set; }
    }

    private async Task Write(Checked check, LoadoutItemGroupId groupId, AbsolutePath extrasFolder, IReadOnlyList<Prepared> prepared, CancellationToken cancellationToken)
    {
        Progress.Enter(InstallPhase.Write, prepared.Count, prepared.Sum(p => p.Placement.Directive.Size));
        var state = LoadExisting(groupId);
        var extras = new ExtrasFolder(extrasFolder);
        using var textureSlots = new SemaphoreSlim(Environment.ProcessorCount);

        foreach (var batch in prepared.Chunk(TransactionSize))
        {
            using var tx = Connection.BeginTransaction();
            using var encodes = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                StartTextures(check, extras, state, batch, textureSlots, encodes.Token);
                foreach (var item in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Execute(check, groupId, state, extras, tx, item, cancellationToken);
                    Progress.Advance(1, item.Placement.Directive.Size);
                }
            }
            finally
            {
                // A batch that stopped early would otherwise sit through every encode it queued before reporting why.
                await encodes.CancelAsync();
                await DiscardTextures(state);
            }

            await FlushPending(state, cancellationToken);
            var result = await tx.Commit();
            foreach (var name in state.NewGroups) state.Groups[name] = result[state.Groups[name]];
            state.NewGroups.Clear();
        }

        await RemoveStaleFiles(state);
    }

    /// <summary>
    /// Deletes the rows an earlier run made that this run did not ask for again: a file whose content changed has a row
    /// for the old hash next to the new one, and a file the list dropped would otherwise stay installed. Runs only after
    /// every batch committed, so an install that failed part way keeps what it had.
    /// </summary>
    private async Task RemoveStaleFiles(WriteState state)
    {
        using var tx = Connection.BeginTransaction();
        var any = false;
        foreach (var known in state.Files.Values.SelectMany(files => files.Values))
        {
            // Rows beyond the first of a wanted file are duplicates a past run left.
            foreach (var id in known.Wanted ? known.Ids.Skip(1) : known.Ids)
            {
                tx.Delete(id, recursive: false);
                any = true;
            }
        }
        if (any) await tx.Commit();
    }

    private WriteState LoadExisting(LoadoutItemGroupId groupId)
    {
        var state = new WriteState();
        var db = Connection.Db;
        foreach (var child in LoadoutItem.FindByParent(db, groupId).OfTypeLoadoutItemGroup())
        {
            var name = child.AsLoadoutItem().Name;
            state.Groups[name] = child.LoadoutItemGroupId;
            var files = state.Files[name] = new();
            foreach (var file in LoadoutItem.FindByParent(db, child.Id).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile())
            {
                var key = (file.AsLoadoutItemWithTargetPath().TargetPath, file.Hash);
                if (!files.TryGetValue(key, out var known)) files[key] = known = new KnownFile();
                known.Ids.Add(file.Id);
            }
        }
        return state;
    }

    private async Task FlushPending(WriteState state, CancellationToken cancellationToken)
    {
        if (state.Pending.Count == 0) return;
        await Writer.Store(state.Pending, cancellationToken);
        state.Pending.Clear();
    }

    /// <summary>
    /// Starts re-encoding the batch's textures together, at most one per core: a BC7 encode is slow enough that doing
    /// them one after another dominates the install. Everything that touches the transaction still happens in order.
    /// </summary>
    private void StartTextures(Checked check, ExtrasFolder extras, WriteState state, IReadOnlyList<Prepared> batch, SemaphoreSlim slots, CancellationToken cancellationToken)
    {
        foreach (var item in batch)
        {
            if (item.Silent || item.Skip is not null || item.Placement.Directive is not TransformedTexture || item.Placement.Target is Mo2Target.Extra) continue;
            state.Textures[item] = EncodeTexture(check, extras, item, slots, cancellationToken);
        }
    }

    private async Task<GeneratedFile> EncodeTexture(Checked check, ExtrasFolder extras, Prepared item, SemaphoreSlim slots, CancellationToken cancellationToken)
    {
        await slots.WaitAsync(cancellationToken);
        try
        {
            return await Writer.Generate(output => WriteBytes(check, extras, item.Placement, item.Member, output, cancellationToken), cancellationToken);
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>Waits out the textures nobody took, because the batch stopped early, and deletes the temp files of those that finished.</summary>
    private static async Task DiscardTextures(WriteState state)
    {
        foreach (var pending in state.Textures.Values)
        {
            try
            {
                await using var file = await pending;
            }
            catch
            {
                // The batch is already failing or was cancelled; the encode's own error is not the one to report.
            }
        }
        state.Textures.Clear();
    }

    private async Task Execute(Checked check, LoadoutItemGroupId modlistGroup, WriteState state, ExtrasFolder extras, ITransaction tx, Prepared item, CancellationToken cancellationToken)
    {
        var placement = item.Placement;
        if (item.Silent) return;

        try
        {
            if (item.Skip is { } skip) throw new DirectiveSkipped(skip.Kind, skip.Reason);
            if (placement.Target is Mo2Target.Extra extra)
            {
                await WriteExtra(check, extras, item, extra, cancellationToken);
                return;
            }
            if (placement.Group is not { } groupName || placement.Path is not { } path)
                throw new InvalidOperationException($"'{placement.To}' reached the write phase without a place in the loadout");

            var (hash, size, xxHash64) = await Produce(check, state, extras, item, cancellationToken);
            state.Written[Fold(placement.To)] = (hash, xxHash64);
            AddFile(tx, modlistGroup, state, groupName, path, hash, size);
        }
        catch (DirectiveSkipped skipped)
        {
            AddSkip(tx, modlistGroup, state, placement, skipped.Kind, skipped.Message, check);
        }
        catch (PatchException patchFailed)
        {
            AddSkip(tx, modlistGroup, state, placement, SkipKind.Transform, $"the patch for '{placement.To}' could not be applied: {patchFailed.Message}", check);
        }
        catch (UnsupportedTextureFormatException unsupported)
        {
            AddSkip(tx, modlistGroup, state, placement, SkipKind.Transform, $"the texture '{placement.To}' could not be re-encoded: {unsupported.Message}", check);
        }
        catch (TransformUnavailableException unavailable)
        {
            AddSkip(tx, modlistGroup, state, placement, SkipKind.Transform, unavailable.Message, check);
        }
        catch (MemberNotFoundException notFound)
        {
            AddSkip(tx, modlistGroup, state, placement, SkipKind.MemberNotFound, notFound.Message, check);
        }
    }

    /// <summary>A tool file goes to the list's folder on disk as it is: the loadout never deploys it, so the file store is not involved.</summary>
    private async Task WriteExtra(Checked check, ExtrasFolder extras, Prepared item, Mo2Target.Extra extra, CancellationToken cancellationToken)
    {
        if (item.Placement.Directive is MergedPatch or CreateBsa)
            throw new DirectiveSkipped(SkipKind.Transform, $"'{item.Placement.To}' is built from other files of the list, which a tool folder does not keep");

        var destination = extras.Root.Combine(extra.Path);
        destination.Parent.CreateDirectory();
        await using var output = destination.Create();
        await WriteBytes(check, extras, item.Placement, item.Member, output, cancellationToken);
    }

    private void AddSkip(ITransaction tx, LoadoutItemGroupId group, WriteState state, Placement placement, SkipKind kind, string reason, Checked check)
    {
        KeepEarlierRows(state, placement);
        _ = new WabbajackSkippedFile.New(tx, out var id)
        {
            LoadoutItem = new LoadoutItem.New(tx, id)
            {
                LoadoutId = TargetLoadout,
                Name = placement.To.FileName.ToString(),
            },
            GroupId = WabbajackModlistLoadoutGroupId.From(group.Value),
            To = placement.To.ToString(),
            Reason = reason,
            Kind = kind,
        };

        // An archive that could not be built takes its inputs with it: they are not installed anywhere else.
        if (placement.Directive is CreateBsa bsa && check.BsaInputs.TryGetValue(Fold(bsa.TempId), out var inputs))
            Progress.AddDropped(inputs.Count);
    }

    /// <summary>
    /// A skip means this run could not produce the file, not that the list stopped asking for it, so whatever an earlier
    /// run installed at that path stays until a run can replace it.
    /// </summary>
    private static void KeepEarlierRows(WriteState state, Placement placement)
    {
        if (placement.Group is not { } groupName || placement.Path is not { } path) return;
        if (!state.Files.TryGetValue(groupName, out var files)) return;
        foreach (var (key, known) in files)
        {
            if (key.Path == path) known.Wanted = true;
        }
    }

    private void AddFile(ITransaction tx, LoadoutItemGroupId modlistGroup, WriteState state, string groupName, GamePath path, Hash hash, Size size)
    {
        if (state.Files.TryGetValue(groupName, out var known) && known.TryGetValue((path, hash), out var existing))
        {
            existing.Wanted = true;
            return;
        }

        if (!state.Groups.TryGetValue(groupName, out var group))
        {
            var created = new LoadoutItemGroup.New(tx, out var id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id)
                {
                    LoadoutId = TargetLoadout,
                    Name = groupName,
                    ParentId = modlistGroup,
                },
            };
            group = state.Groups[groupName] = created.LoadoutItemGroupId;
            state.NewGroups.Add(groupName);
            state.Files[groupName] = known = new();
        }
        known ??= state.Files[groupName] = new();

        _ = new LoadoutFile.New(tx, out var fileId)
        {
            LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, fileId)
            {
                LoadoutItem = new LoadoutItem.New(tx, fileId)
                {
                    LoadoutId = TargetLoadout,
                    ParentId = group,
                    Name = path.FileName,
                },
                TargetPath = path.ToGamePathParentTuple(TargetLoadout),
            },
            Hash = hash,
            Size = size,
        };
        known[(path, hash)] = new KnownFile { Wanted = true };
    }

    #endregion

    #region Tools

    /// <summary>
    /// Registers the executables the list's ModOrganizer.ini names as tools of the installation, once their files are on
    /// disk. A tool that is already registered is left as it is, so installing again adds nothing.
    /// </summary>
    private async Task RegisterTools(Checked check, AbsolutePath extrasFolder, CancellationToken cancellationToken)
    {
        var iniText = await ReadModOrganizerIni(check, cancellationToken);
        if (iniText is null) return;

        var installation = check.Loadout.InstallationInstance;
        var extras = new ExtrasFolder(extrasFolder);
        var gameRoot = installation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, RelativePath.Empty));
        var primary = installation.Game.GetPrimaryFile(installation);

        var found = ModlistTools.Find(iniText, extras.Root, gameRoot, primary.FileName.ToString());
        UnregisteredTools = found.Unregistered;
        foreach (var line in found.Unregistered) Logger.LogWarning("Not registering a tool of '{List}': {Reason}", check.List.Name, line);

        var db = Connection.Db;
        var known = UserTool.FindByInstallation(db, check.Loadout.InstallationId)
            .Select(t => (t.Name, t.Exe, t.Arguments)).ToHashSet();

        using var tx = Connection.BeginTransaction();
        var any = false;
        foreach (var tool in found.Tools.Where(t => !known.Contains((t.Name, t.Exe, t.Arguments))))
        {
            _ = new UserTool.New(tx)
            {
                InstallationId = check.Loadout.InstallationId,
                Name = tool.Name,
                Exe = tool.Exe,
                Arguments = tool.Arguments,
                RunVia = ToolRunVia.Auto,
            };
            any = true;
        }
        if (any) await tx.Commit();
    }

    private async Task<string?> ReadModOrganizerIni(Checked check, CancellationToken cancellationToken)
    {
        foreach (var placement in check.Placements)
        {
            if (placement.Target is not Mo2Target.Seed { Kind: SeedKind.ModOrganizerIni } || placement.Directive is not InlineFile inline) continue;

            await using var stream = await ModlistArchive.ReadBlob(FileStore, check.ListArchive, inline.SourceDataId, cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken);
        }
        return null;
    }

    #endregion

    #region Order

    private sealed record ProfileTexts(string? ModlistTxt, string? PluginsTxt, string? LoadorderTxt);

    /// <summary>
    /// Puts the mod groups in the order MO2 would give them, stages the profile's plugin list on the loadout's own plugin
    /// order, and enables the list when every directive was executed. Every write is in one transaction, so a failure
    /// leaves the list disabled and the next run does it again.
    /// </summary>
    private async Task Order(Checked check, LoadoutItemGroupId modlistGroup, CancellationToken cancellationToken)
    {
        Progress.Enter(InstallPhase.Order, 0, 0);
        var installation = check.Loadout.InstallationInstance;
        var target = RequireTarget(installation);
        var texts = await ReadProfileTexts(check, cancellationToken);

        // The sort order manager creates the loadout's order a moment after the loadout appears, so it may not be there yet.
        var sortOrderId = await RequirePluginVariety(installation).GetOrCreateSortOrderFor(TargetLoadout, TargetLoadout, cancellationToken);

        var db = Connection.Db;
        var children = LoadoutItem.FindByParent(db, modlistGroup).OfTypeLoadoutItemGroup().ToArray();
        var entries = texts.ModlistTxt is null ? [] : ModlistTxt.Parse(texts.ModlistTxt);
        var ordered = PriorityOrder(check, children, entries);

        using var tx = Connection.BeginTransaction();
        WritePriorities(db, tx, ordered);

        var disabledByList = entries.Where(e => !e.Enabled && !e.IsSeparator && !e.IsForeign).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var enabledByList = entries.Where(e => e.Enabled && !e.IsSeparator && !e.IsForeign).Select(e => e.Name)
            .Where(name => !disabledByList.Contains(name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var child in children)
        {
            var name = child.AsLoadoutItem().Name;
            if (disabledByList.Contains(name))
                tx.Add(child.Id, LoadoutItem.Disabled, Null.Instance);
            else if (enabledByList.Contains(name) && child.AsLoadoutItem().IsDisabled)
                tx.Retract(child.Id, LoadoutItem.Disabled, Null.Instance);
        }

        if (texts.PluginsTxt is not null || texts.LoadorderTxt is not null)
            target.StagePluginList(Loadout.Load(db, TargetLoadout), sortOrderId, texts.PluginsTxt, texts.LoadorderTxt, tx);
        else
            Logger.LogWarning("The profile of '{List}' has neither a plugins.txt nor a loadorder.txt, so the plugin order is left as it was", check.List.Name);

        tx.Add(modlistGroup.Value, WabbajackModlistLoadoutGroup.InstalledAt, DateTimeOffset.UtcNow);
        if (!WabbajackSkippedFile.FindByGroup(db, modlistGroup).Any() && LoadoutItem.Load(db, modlistGroup).IsDisabled)
            tx.Retract(modlistGroup.Value, LoadoutItem.Disabled, Null.Instance);

        await tx.Commit();
    }

    /// <summary>
    /// The groups from lowest priority to highest: game-root groups, profile settings, mods with no line in modlist.txt,
    /// the listed mods bottom-up (modlist.txt is written top-down, and the first line wins), then Overwrite.
    /// </summary>
    private List<LoadoutItemGroup.ReadOnly> PriorityOrder(Checked check, IReadOnlyList<LoadoutItemGroup.ReadOnly> children, IReadOnlyList<ModlistTxtEntry> entries)
    {
        var byName = children.ToDictionary(c => c.AsLoadoutItem().Name, StringComparer.OrdinalIgnoreCase);
        var rootNames = check.Placements.Select(p => p.Target).OfType<Mo2Target.GameRootFile>().Select(t => t.GroupName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<LoadoutItemGroup.ReadOnly>(children.Count);
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Place(string name)
        {
            if (byName.TryGetValue(name, out var group) && placed.Add(name)) ordered.Add(group);
        }

        foreach (var name in rootNames.Order(StringComparer.OrdinalIgnoreCase)) Place(name);
        Place(Mo2Layout.ProfileSettingsGroup);

        var listed = entries.Where(e => !e.IsSeparator && !e.IsForeign).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unlisted = children
            .Select(c => c.AsLoadoutItem().Name)
            .Where(name => !placed.Contains(name) && !listed.Contains(name) && !name.Equals(Mo2Layout.OverwriteGroup, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var name in unlisted)
        {
            Logger.LogWarning("Mod '{Mod}' of '{List}' has no line in modlist.txt, so it is placed below the listed mods", name, check.List.Name);
            Place(name);
        }

        foreach (var entry in entries.Reverse().Where(e => !e.IsSeparator && !e.IsForeign)) Place(entry.Name);
        Place(Mo2Layout.OverwriteGroup);
        return ordered;
    }

    /// <summary>
    /// Gives each group one priority row. A first install counts up from above every row the loadout holds for other
    /// groups. A repeat install hands the same values back out in the new order, so the list keeps its place below mods
    /// installed since; only groups past the count it already held take fresh values above every row.
    /// </summary>
    private void WritePriorities(IDb db, ITransaction tx, IReadOnlyList<LoadoutItemGroup.ReadOnly> ordered)
    {
        var own = ordered.Select(g => g.LoadoutItemGroupId).ToHashSet();
        var rows = LoadoutItemGroupPriority.FindByLoadout(db, TargetLoadout).ToArray();
        var held = rows.Where(r => own.Contains(r.TargetId)).Select(r => r.Priority.Value).Order().ToArray();
        var next = rows.Select(r => r.Priority.Value).DefaultIfEmpty(0UL).Max() + 1;
        var mine = rows.Where(r => own.Contains(r.TargetId)).ToLookup(r => r.TargetId);

        for (var i = 0; i < ordered.Count; i++)
        {
            var group = ordered[i];
            var priority = ConflictPriority.From(i < held.Length ? held[i] : next++);
            if (mine.Contains(group.LoadoutItemGroupId))
            {
                tx.Add(mine[group.LoadoutItemGroupId].First().Id, LoadoutItemGroupPriority.Priority, priority);
                continue;
            }

            _ = new LoadoutItemGroupPriority.New(tx)
            {
                LoadoutId = TargetLoadout,
                TargetId = group.LoadoutItemGroupId,
                Priority = priority,
            };
        }
    }

    private async Task<ProfileTexts> ReadProfileTexts(Checked check, CancellationToken cancellationToken)
    {
        string? modlist = null, plugins = null, loadorder = null;
        foreach (var placement in check.Placements)
        {
            if (placement.Target is not Mo2Target.Seed seed || placement.Directive is not InlineFile inline) continue;
            if (seed.Kind is not (SeedKind.ModlistTxt or SeedKind.PluginsTxt or SeedKind.LoadorderTxt)) continue;

            await using var stream = await ModlistArchive.ReadBlob(FileStore, check.ListArchive, inline.SourceDataId, cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync(cancellationToken);
            switch (seed.Kind)
            {
                case SeedKind.ModlistTxt: modlist = text; break;
                case SeedKind.PluginsTxt: plugins = text; break;
                default: loadorder = text; break;
            }
        }
        return new ProfileTexts(modlist, plugins, loadorder);
    }

    #endregion

    #region Producing bytes

    /// <summary>The list's extras folder and the downloads folder inside it, created when first needed.</summary>
    private sealed class ExtrasFolder(AbsolutePath root)
    {
        public AbsolutePath Root => root;

        public AbsolutePath Downloads
        {
            get
            {
                var downloads = root.Combine("downloads");
                downloads.CreateDirectory();
                return downloads;
            }
        }
    }

    /// <summary>The file store hash and size of what the directive writes, producing and storing the bytes when they are not a member already in the store.</summary>
    private async Task<(Hash Hash, Size Size, Hash XxHash64)> Produce(Checked check, WriteState state, ExtrasFolder extras, Prepared item, CancellationToken cancellationToken)
    {
        var directive = item.Placement.Directive;
        switch (directive)
        {
            case FromArchive when directive.GetType() == typeof(FromArchive):
            {
                var member = RequireMember(item.Placement, item.Member);
                return (member.StoreHash, member.Size, member.XxHash64 ?? directive.XxHash64);
            }

            case MergedPatch merged:
                return await Generated(state, directive, await MergedBytes(check, state, merged, cancellationToken), checkHash: true, cancellationToken);

            case CreateBsa bsa:
                return await Generated(state, directive, output => BuildArchive(check, extras, bsa, output, cancellationToken), checkHash: false, cancellationToken);

            case TransformedTexture when state.Textures.Remove(item, out var encoding):
                return await Register(state, directive, await encoding, checkHash: false, cancellationToken);

            default:
                var exempt = directive is RemappedInlineFile or TransformedTexture || IsHashUnchecked(directive);
                return await Generated(state, directive, output => WriteBytes(check, extras, item.Placement, item.Member, output, cancellationToken), checkHash: !exempt, cancellationToken);
        }
    }

    private static bool IsHashUnchecked(Directive directive)
        => directive is InlineFile or ArchiveMeta && HashUnchecked.Contains(directive.To.FileName.ToString(), StringComparer.OrdinalIgnoreCase);

    private async Task<(Hash, Size, Hash)> Generated(WriteState state, Directive directive, Func<Stream, Task> produce, bool checkHash, CancellationToken cancellationToken)
        => await Register(state, directive, await Writer.Generate(produce, cancellationToken), checkHash, cancellationToken);

    /// <summary>Queues a generated file for the file store, after checking it against the directive's hash when the list promises one.</summary>
    private async Task<(Hash, Size, Hash)> Register(WriteState state, Directive directive, GeneratedFile file, bool checkHash, CancellationToken cancellationToken)
    {
        if (checkHash && file.XxHash64 != directive.XxHash64)
        {
            await file.DisposeAsync();
            throw new DirectiveSkipped(SkipKind.HashMismatch, $"the file made for '{directive.To}' does not match the one the list was made with");
        }

        // A texture recompressed here need not match the author's bytes; the list never checks it either.
        if (directive is TransformedTexture && file.XxHash64 != directive.XxHash64)
            Logger.LogInformation("Texture {Path} was recompressed to {Actual} where the list records {Expected}", directive.To, file.XxHash64, directive.XxHash64);

        state.Pending.Add(file);
        if (state.Pending.Count >= GeneratedFileWriter.StoreBatchSize) await FlushPending(state, cancellationToken);
        return (file.XxHash3, file.Size, file.XxHash64);
    }

    /// <summary>The bytes of one directive other than a merge or an archive, written to <paramref name="output"/>.</summary>
    private async Task WriteBytes(Checked check, ExtrasFolder extras, Placement placement, ResolvedMember? member, Stream output, CancellationToken cancellationToken)
    {
        switch (placement.Directive)
        {
            case PatchedFromArchive patched:
            {
                await using var basis = await RequireMember(placement, member).Open();
                await using var delta = await ModlistArchive.ReadBlob(FileStore, check.ListArchive, patched.PatchId, cancellationToken);
                await Transforms.Patch(basis, delta, output, cancellationToken);
                break;
            }
            case TransformedTexture texture:
            {
                await using var dds = await RequireMember(placement, member).Open();
                await Transforms.Recompress(dds, texture.Image, output, cancellationToken);
                break;
            }
            case FromArchive:
            {
                await using var source = await RequireMember(placement, member).Open();
                await source.CopyToAsync(output, cancellationToken);
                break;
            }
            case RemappedInlineFile remapped:
            {
                await using var blob = await ModlistArchive.ReadBlob(FileStore, check.ListArchive, remapped.SourceDataId, cancellationToken);
                using var reader = new StreamReader(blob, Encoding.UTF8);
                var text = PathMarkers.Substitute(await reader.ReadToEndAsync(cancellationToken), MarkerValues(check, extras), nativeSpelling: false);
                await output.WriteAsync(new UTF8Encoding(false).GetBytes(text), cancellationToken);
                break;
            }
            case InlineFile inline:
            {
                await using var blob = await ModlistArchive.ReadBlob(FileStore, check.ListArchive, inline.SourceDataId, cancellationToken);
                await blob.CopyToAsync(output, cancellationToken);
                break;
            }
            case ArchiveMeta meta:
            {
                await using var blob = await ModlistArchive.ReadBlob(FileStore, check.ListArchive, meta.SourceDataId, cancellationToken);
                await blob.CopyToAsync(output, cancellationToken);
                break;
            }
            default:
                throw new DirectiveSkipped(SkipKind.Transform, $"a {placement.Directive.GetType().Name} cannot be used as a file of an archive");
        }
    }

    private static ResolvedMember RequireMember(Placement placement, ResolvedMember? member)
        => member ?? throw new InvalidOperationException($"'{placement.To}' reads an archive member that was never resolved");

    private PathMarkerValues MarkerValues(Checked check, ExtrasFolder extras)
    {
        var installation = check.Loadout.InstallationInstance;
        var gameRoot = installation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, RelativePath.Empty));

        // Without a prefix the translator leaves a Z: path, which Wine maps to the host root.
        var resolution = PrefixResolver.Resolve(installation);
        var prefixRoot = resolution.Prefix?.Path.ToString() ?? "";
        return new PathMarkerValues(
            GamePath: WindowsPathTranslator.Translate(gameRoot.ToString(), prefixRoot),
            Mo2Path: WindowsPathTranslator.Translate(extras.Root.ToString(), prefixRoot),
            DownloadsPath: WindowsPathTranslator.Translate(extras.Downloads.ToString(), prefixRoot));
    }

    /// <summary>The sources a merge names, already written by earlier directives, joined in the order the list gives.</summary>
    private async Task<Func<Stream, Task>> MergedBytes(Checked check, WriteState state, MergedPatch merged, CancellationToken cancellationToken)
    {
        var hashes = new List<Hash>(merged.Sources.Count);
        foreach (var source in merged.Sources)
        {
            if (!state.Written.TryGetValue(Fold(source.Path), out var written))
                throw new DirectiveSkipped(SkipKind.Transform, $"'{source.Path}', which '{merged.To}' merges, was not installed");
            if (written.XxHash64 != source.XxHash64)
                throw new DirectiveSkipped(SkipKind.Transform, $"'{source.Path}', which '{merged.To}' merges, is not the file the list was made with");
            hashes.Add(written.Store);
        }

        return async output =>
        {
            var joined = await Writer.WriteTemporary(async whole =>
            {
                foreach (var hash in hashes)
                {
                    await using var part = await FileStore.GetFileStream(hash, cancellationToken);
                    await part.CopyToAsync(whole, cancellationToken);
                }
            });
            await using (joined)
            {
                await using var basis = joined.Path.Read();
                await using var delta = await ModlistArchive.ReadBlob(FileStore, check.ListArchive, merged.PatchId, cancellationToken);
                await Transforms.Patch(basis, delta, output, cancellationToken);
            }
        };
    }

    private async Task BuildArchive(Checked check, ExtrasFolder extras, CreateBsa bsa, Stream output, CancellationToken cancellationToken)
    {
        var inputs = check.BsaInputs.GetValueOrDefault(Fold(bsa.TempId)) ?? new Dictionary<string, Placement>();
        var rows = check.Plan.Rows.GroupBy(r => r.Archive.XxHash64).ToDictionary(g => g.Key, g => g.First());
        var spilled = new List<TemporaryPath>();
        try
        {
            async Task<Stream> OpenInput(RelativePath path)
            {
                if (!inputs.TryGetValue(Fold(path), out var input))
                    throw new DirectiveSkipped(SkipKind.MemberNotFound, $"the archive '{bsa.To}' needs '{path}', which no directive writes");

                var member = input.Directive is FromArchive from ? await ResolveMember(rows, from, cancellationToken) : null;
                var temporary = await Writer.WriteTemporary(file => WriteBytes(check, extras, input, member, file, cancellationToken));
                spilled.Add(temporary);
                return temporary.Path.Read();
            }

            await Transforms.BuildBsa(bsa.State, bsa.FileStates, OpenInput, output, cancellationToken);
        }
        finally
        {
            foreach (var temporary in spilled) await temporary.DisposeAsync();
        }
    }

    #endregion

    private static string Fold(RelativePath path) => path.ToString().Replace('\\', '/').ToLowerInvariant();
}
