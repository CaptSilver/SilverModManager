using System.Text;
using SilverModManager.Sdk.Hashes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Games.Modlists;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.ToolRunner;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Mapping;
using SilverModManager.Wabbajack.Models;
using BethesdaArchiveException = SilverModManager.Games.Bethesda.Archives.BethesdaArchiveException;
using PatchException = SilverModManager.Wabbajack.Transforms.PatchException;
using UnsupportedTextureFormatException = SilverModManager.Wabbajack.Transforms.UnsupportedTextureFormatException;

namespace SilverModManager.Wabbajack.Install;

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
    public required ModOrderImporter OrderImporter { get; init; }
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
            OrderImporter = provider.GetRequiredService<ModOrderImporter>(),
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
            Mo2Layout.GameRootFolders(installation, target, tos),
            target.ProfileSettingsFiles(installation),
            relative => target.ModFilePath(installation, relative));

        var placements = Classify(list, tos, context);
        var bsaInputs = CollectBsaInputs(placements);

        // Mod Organizer 2's own files are the app's job: they are dropped here and its archive is not fetched.
        var program = Mo2Program.Find(list, context.GameRootFolders);
        placements = DropProgramFiles(placements, program, context.GameRootFolders);
        var gameFiles = Inventory.GameFilesFor(loadout);
        var plan = await Inventory.Build(list, Connection.Db, gameFiles, cancellationToken, program: program);
        var missing = plan.Rows.Where(r => r.State is not (ArchiveState.Have or ArchiveState.GameFile or ArchiveState.NotNeeded)).ToArray();
        if (missing.Length > 0 && !Options.SkipMissingArchives) throw new ArchivesMissingException(missing);

        var (estimate, isLowerBound) = EstimateExtractedSize(plan);
        Progress.SetEstimate(estimate, isLowerBound);
        CountPathsNeverInstalled(placements, list);

        return new Checked(loadout, list, listArchive, plan, gameFiles, placements, bsaInputs);
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
                Mo2Target.ProfileTweak tweak => (Mo2Layout.ProfileSettingsGroup, tweak.Path),
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

    /// <summary>
    /// A file taken from a Mod Organizer 2 release that lands in the program or a tool folder goes nowhere, even when
    /// the release is kept for another file: the app never installs the program.
    /// </summary>
    private static IReadOnlyList<Placement> DropProgramFiles(IReadOnlyList<Placement> placements, Mo2ProgramArchives program, IReadOnlySet<string> gameRootFolders)
        => placements.Select(p => p.Directive is FromArchive from
                                  && program.IsRecognised(from.Source.ArchiveXxHash64)
                                  && Mo2Program.IsProgramLocation(p.To, gameRootFolders)
            ? p with { Target = new Mo2Target.Dropped("Mod Organizer 2 program file"), Group = null, Path = null }
            : p).ToArray();

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
                case ArchiveState.GameFile or ArchiveState.GameFileVersion or ArchiveState.NotNeeded:
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
            .Where(p => p.Target is Mo2Target.ModFile or Mo2Target.ProfileSetting or Mo2Target.ProfileTweak or Mo2Target.GameRootFile or Mo2Target.Extra)
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

    private async Task<Prepared> Prepare(Checked check, Dictionary<FileIdentity, ArchivePlanRow> rows, Placement placement, CancellationToken cancellationToken)
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
        if (described is { } mine && mine.XxHash64.Equals(directive.XxHash64))
            return new Prepared(placement, null, null, Silent: true);

        var reason = row.Reason ?? $"'{gameFile.GameFile}' differs from the list's copy (game version {gameFile.GameVersion})";
        return new Prepared(placement, null, new Skip(SkipKind.GameFileVersion, reason), Silent: false);
    }

    /// <summary>The member a FromArchive-family directive reads, or <see cref="DirectiveSkipped"/> saying why there is none.</summary>
    private async Task<ResolvedMember> ResolveMember(Dictionary<FileIdentity, ArchivePlanRow> rows, FromArchive directive, CancellationToken cancellationToken)
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
            _ => (FileIdentity?)null,
        };
        if (expected is null) return member;
        if (member.XxHash64 is not { } actual || !actual.Equals(expected.Value))
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
        public Dictionary<string, (FileIdentity Store, FileIdentity XxHash64)> Written { get; } = new();

        /// <summary>Textures of the batch being written that are already being re-encoded, taken by <see cref="Produce"/> when it reaches them.</summary>
        public Dictionary<Prepared, Task<GeneratedFile>> Textures { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>Generated files waiting for the next batch to the file store.</summary>
        public List<GeneratedFile> Pending { get; } = new();

        /// <summary>Mod group name to its id: real once committed, a temp id while its transaction is open.</summary>
        public Dictionary<string, LoadoutItemGroupId> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> NewGroups { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per group, the files an earlier run (or this one) already created, as path and hash.</summary>
        public Dictionary<string, Dictionary<(GamePath Path, FileIdentity Identity), KnownFile>> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
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
            // The settings group is found by its marker; a user may have renamed it.
            var name = child.IsGameSettingsGroup ? Mo2Layout.ProfileSettingsGroup : child.AsLoadoutItem().Name;
            state.Groups[name] = child.LoadoutItemGroupId;
            var files = state.Files[name] = new();
            foreach (var file in LoadoutItem.FindByParent(db, child.Id).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile())
            {
                var key = (file.AsLoadoutItemWithTargetPath().TargetPath, file.Identity);
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
            if (placement.Target is Mo2Target.ProfileSetting && OwnedIniPath(check, path) is { } iniPath)
                SetIniBaseline(tx, modlistGroup, state, groupName, check, iniPath, hash);
            else
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
        catch (BethesdaArchiveException archiveFailed)
        {
            AddSkip(tx, modlistGroup, state, placement, SkipKind.Transform, $"the archive '{placement.To}' could not be built: {archiveFailed.Message}", check);
        }
        catch (ArchiveMismatchException mismatch)
        {
            AddSkip(tx, modlistGroup, state, placement, SkipKind.Transform, $"the archive '{placement.To}' was built wrong: {mismatch.Message}", check);
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

    /// <summary>
    /// The path the loadout's bake owns that <paramref name="path"/> names, spelled as the game spells it, or null when
    /// the game keeps no copy of that file per loadout.
    /// </summary>
    private static GamePath? OwnedIniPath(Checked check, GamePath path)
    {
        if (check.Loadout.InstallationInstance.Game is not IGameIniFiles game) return null;
        foreach (var owned in GameInis.Paths(game))
        {
            if (owned == path) return owned;
        }
        return null;
    }

    /// <summary>
    /// Makes <paramref name="identity"/> the loadout's baseline for a game INI. A file at the INI's real path would be a
    /// second writer next to the bake, so the profile's INI is kept as the bake's starting point and the settings group
    /// holds only what is not an INI of the game (the loadout's tweak).
    /// </summary>
    private void SetIniBaseline(ITransaction tx, LoadoutItemGroupId modlistGroup, WriteState state, string groupName, Checked check, GamePath iniPath, FileIdentity identity)
    {
        EnsureGroup(tx, modlistGroup, state, groupName);

        if (GameInis.StateOf(check.Loadout, iniPath) is { } existing)
        {
            tx.Add(existing.Id, LoadoutIniState.Baseline, identity.XxHash3());
            return;
        }

        _ = new LoadoutIniState.New(tx)
        {
            LoadoutId = TargetLoadout,
            Path = iniPath.ToGamePathParentTuple(TargetLoadout),
            Baseline = identity.XxHash3(),
        };
    }

    private LoadoutItemGroupId EnsureGroup(ITransaction tx, LoadoutItemGroupId modlistGroup, WriteState state, string groupName)
    {
        if (state.Groups.TryGetValue(groupName, out var group)) return group;

        var isSettings = groupName.Equals(Mo2Layout.ProfileSettingsGroup, StringComparison.OrdinalIgnoreCase);
        var created = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id)
            {
                LoadoutId = TargetLoadout,
                Name = isSettings ? Mo2Layout.GameSettingsGroupName : groupName,
                ParentId = modlistGroup,
            },
        };
        if (isSettings) tx.Add(id, LoadoutItemGroup.GameSettingsGroup, Null.Instance);
        group = state.Groups[groupName] = created.LoadoutItemGroupId;
        state.NewGroups.Add(groupName);
        state.Files[groupName] = new();
        return group;
    }

    private void AddFile(ITransaction tx, LoadoutItemGroupId modlistGroup, WriteState state, string groupName, GamePath path, FileIdentity identity, Size size)
    {
        if (state.Files.TryGetValue(groupName, out var known) && known.TryGetValue((path, identity), out var existing))
        {
            existing.Wanted = true;
            return;
        }

        var group = EnsureGroup(tx, modlistGroup, state, groupName);
        known = state.Files[groupName];

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
            Hash = identity.XxHash3(),
            Size = size,
        };
        known[(path, identity)] = new KnownFile { Wanted = true };
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
    /// Puts the mod groups in the order MO2 would give them and switches off the ones its modlist.txt disables, stages the
    /// profile's plugin list on the loadout's own plugin order, and enables the list when every directive was executed.
    /// Every write is in one transaction, so a failure leaves the list disabled and the next run does it again.
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
        var rootNames = check.Placements.Select(p => p.Target).OfType<Mo2Target.GameRootFile>().Select(t => t.GroupName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var order = Mo2ProfileOrderReader.Read(children, entries, rootNames, check.List.Name, Logger);

        // The importer's priority write must be the only one in this transaction, so it opens fresh. The profile's own plugin
        // order is staged below, so the importer must not seed the plugin variety from the mod order as well.
        using var tx = Connection.BeginTransaction();
        // Read fresh: the groups this run created are not in the loadout the check loaded, and the sink names them in its drops.
        OrderImporter.Import(Loadout.Load(db, TargetLoadout), order, new ModOrderSource($"Wabbajack list {check.List.Name}", modlistGroup, Manifest: null, SeedVarieties: false), tx);

        if (texts.PluginsTxt is not null || texts.LoadorderTxt is not null)
            target.StagePluginList(Loadout.Load(db, TargetLoadout), sortOrderId, texts.PluginsTxt, texts.LoadorderTxt, tx);
        else
            Logger.LogWarning("The profile of '{List}' has neither a plugins.txt nor a loadorder.txt, so the plugin order is left as it was", check.List.Name);

        tx.Add(modlistGroup.Value, WabbajackModlistLoadoutGroup.InstalledAt, DateTimeOffset.UtcNow);
        if (!WabbajackSkippedFile.FindByGroup(db, modlistGroup).Any() && LoadoutItem.Load(db, modlistGroup).IsDisabled)
            tx.Retract(modlistGroup.Value, LoadoutItem.Disabled, Null.Instance);

        await tx.Commit();
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
    private async Task<(FileIdentity Identity, Size Size, FileIdentity XxHash64)> Produce(Checked check, WriteState state, ExtrasFolder extras, Prepared item, CancellationToken cancellationToken)
    {
        var directive = item.Placement.Directive;
        switch (directive)
        {
            case FromArchive when directive.GetType() == typeof(FromArchive):
            {
                var member = RequireMember(item.Placement, item.Member);
                return (member.StoreIdentity, member.Size, member.XxHash64 ?? directive.XxHash64);
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

    private async Task<(FileIdentity, Size, FileIdentity)> Generated(WriteState state, Directive directive, Func<Stream, Task> produce, bool checkHash, CancellationToken cancellationToken)
        => await Register(state, directive, await Writer.Generate(produce, cancellationToken), checkHash, cancellationToken);

    /// <summary>Queues a generated file for the file store, after checking it against the directive's hash when the list promises one.</summary>
    private async Task<(FileIdentity, Size, FileIdentity)> Register(WriteState state, Directive directive, GeneratedFile file, bool checkHash, CancellationToken cancellationToken)
    {
        if (checkHash && !file.XxHash64.Equals(directive.XxHash64))
        {
            await file.DisposeAsync();
            throw new DirectiveSkipped(SkipKind.HashMismatch, $"the file made for '{directive.To}' does not match the one the list was made with");
        }

        // A texture recompressed here need not match the author's bytes; the list never checks it either.
        if (directive is TransformedTexture && !file.XxHash64.Equals(directive.XxHash64))
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
        var hashes = new List<FileIdentity>(merged.Sources.Count);
        foreach (var source in merged.Sources)
        {
            if (!state.Written.TryGetValue(Fold(source.Path), out var written))
                throw new DirectiveSkipped(SkipKind.Transform, $"'{source.Path}', which '{merged.To}' merges, was not installed");
            if (!written.XxHash64.Equals(source.XxHash64))
                throw new DirectiveSkipped(SkipKind.Transform, $"'{source.Path}', which '{merged.To}' merges, is not the file the list was made with");
            hashes.Add(written.Store);
        }

        return async output =>
        {
            // A source this batch generated (a patched or inline file) may still be waiting for the file store.
            await FlushPending(state, cancellationToken);
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
