using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Channels;
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
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Mapping;
using SilverModManager.Wabbajack.Models;
using BethesdaArchiveException = SilverModManager.Games.Bethesda.Archives.BethesdaArchiveException;
using FileState = SilverModManager.Games.Bethesda.Archives.FileState;
using Ba2Dx10FileState = SilverModManager.Games.Bethesda.Archives.Ba2Dx10FileState;
using PatchException = SilverModManager.Wabbajack.Transforms.PatchException;
using UnreadableTextureException = SilverModManager.Wabbajack.Transforms.UnreadableTextureException;
using UnsupportedTextureFormatException = SilverModManager.Wabbajack.Transforms.UnsupportedTextureFormatException;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Wabbajack.Install;

/// <summary>
/// Installs a modlist's archive members and inline files into a loadout as mod groups under a
/// <see cref="WabbajackModlistLoadoutGroup"/>. A directive the app cannot execute leaves a
/// <see cref="WabbajackSkippedFile"/> row. Once the files are written the groups are put in the order the profile's
/// modlist.txt gives, the profile's plugin list is staged, and the list is enabled unless something was skipped.
/// Running the job again on the same loadout continues in the same group and brings it in line with the list: files the
/// list still installs are kept, ones whose content changed or that the list no longer installs are removed.
/// </summary>
/// <summary>
/// A commit ends the batch of files written to the loadout when any limit is reached. Each commit makes the loadout
/// recompute, so fewer is cheaper; the limits keep a long install from holding hours of work in one transaction, since
/// what is committed is what a later run finds and keeps.
/// </summary>
public sealed record CommitPolicy(int MaxItems, long MaxBytes, TimeSpan MaxInterval)
{
    public static readonly CommitPolicy Default = new(10_000, 2L * 1024 * 1024 * 1024, TimeSpan.FromMinutes(2));
}

public sealed class InstallModlistJob : IJobDefinitionWithStart<InstallModlistJob, WabbajackModlistLoadoutGroup.ReadOnly>
{
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

    /// <summary>What the install may hold in memory at once; the budget for this machine when not set.</summary>
    public ByteBudget? MemoryBudget { get; init; }

    /// <summary>How many directives are resolved and produced at once; one per processor when not set.</summary>
    public int? Parallelism { get; init; }

    /// <summary>When the files written so far are committed to the loadout.</summary>
    public CommitPolicy Commits { get; init; } = CommitPolicy.Default;

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
        var monitor = provider.GetRequiredService<IJobMonitor>();
        provider.GetRequiredService<ModlistInstallRuns>().EnsureNoneInto(target);

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
        return monitor.Begin<InstallModlistJob, WabbajackModlistLoadoutGroup.ReadOnly>(job);
    }

    public async ValueTask<WabbajackModlistLoadoutGroup.ReadOnly> StartAsync(IJobContext<InstallModlistJob> context)
    {
        var cancellationToken = context.CancellationToken;
        using var progressLog = Progress.Changes.Subscribe(new InstallProgressLog(Logger, Modlist.Name).Report);

        var check = await Check(cancellationToken);
        var (group, extrasFolder) = await EnsureGroup(check, cancellationToken);

        try
        {
            // What an earlier run committed is read first: the directives it finished are left out of both phases.
            var state = LoadExisting(group);
            var prepared = await Resolve(check, state, cancellationToken);
            await Write(check, group, extrasFolder, state, prepared, cancellationToken);
            await RegisterTools(check, group, extrasFolder, cancellationToken);
            await Order(check, group, cancellationToken);
        }
        finally
        {
            if (_resolver is { } resolver)
            {
                // Hashes computed before a stop are worth keeping; the next run would otherwise read the same files again.
                try
                {
                    await resolver.FlushDigests(CancellationToken.None);
                }
                catch (Exception e)
                {
                    Logger.LogWarning(e, "Could not record the hashes computed for the library files of '{List}'", Modlist.Name);
                }

                // Stops copies still running, then releases the pins on the bytes the resolver backed up; the loadout files now reference them.
                await resolver.DisposeAsync();
            }
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
        // A game file of another version is not a download to fetch: its directives are skipped with that reason instead.
        var missing = plan.Rows.Where(r => r.State is not (ArchiveState.Have or ArchiveState.GameFile or ArchiveState.GameFileVersion or ArchiveState.NotNeeded)).ToArray();
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
            throw new UnsupportedModlistException($"Wabbajack lists for {MarkdownText.Code(name)} are not supported yet: the game cannot lay out a Mod Organizer instance");
        if (target.UnsupportedReason(installation) is { } reason)
            throw new UnsupportedModlistException($"Wabbajack lists for {MarkdownText.Code(name)} are not supported yet: {MarkdownText.Inline(reason)}");
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
            ?? throw new UnsupportedModlistException($"Wabbajack lists for {MarkdownText.Code(list.GameType)} are not supported yet: this app has no title for that game");
        if (listGame != installation.Game.GameId)
            throw new UnsupportedModlistException($"The list is for {MarkdownText.Code(list.GameType)}, but this loadout is for {installation.Game.DisplayName}");
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
        var unbuilt = placements.Where(p => p.Target is Mo2Target.BsaInput input && !built.Contains(Fold(input.TempId))).ToArray();
        Progress.AddDropped(unbuilt.Length);
        foreach (var group in unbuilt.GroupBy(p => ((Mo2Target.BsaInput)p.Target).TempId.ToString()))
        {
            Logger.LogWarning("Dropped {Count} file(s) for archive '{TempId}' of '{List}' because no directive builds it: {Inputs}",
                group.Count(), group.Key, list.Name, string.Join(", ", group.Select(p => p.To.ToString()).Take(SkipExamplesLogged)));
        }
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
            {
                // The note is deleted with the other rows, so the directive it is about has to run again to write it.
                if (skipped.Kind == SkipKind.KeptOriginal) _keptOriginals.Add(skipped.To);
                tx.Delete(skipped.Id, recursive: false);
            }
            // A resume is an unfinished install again until it ends; with InstalledAt left in place it would read as
            // installed from the moment its skip rows are gone.
            if (found.InstalledAt is { HasValue: true } finishedAt)
                tx.Retract(found.Id, WabbajackModlistLoadoutGroup.InstalledAt, finishedAt.Value);
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

    /// <param name="ArchiveEstimate">For an archive build, its <see cref="WorkEstimate.OfArchive"/> size worked out from the members its inputs read, so Admit can reserve for it without reading anything and the members themselves are not kept.</param>
    private sealed record Prepared(Placement Placement, ResolvedMember? Member, Skip? Skip, bool Silent, long? ArchiveEstimate = null);

    private sealed class DirectiveSkipped(SkipKind kind, string reason) : Exception(reason)
    {
        public SkipKind Kind { get; } = kind;
    }

    private ArchivePathResolver NewResolver(Checked check)
        => new(check.Plan, Connection.Db, FileStore, check.GameFiles, Openers, Logger);

    private ArchivePathResolver? _resolver;

    private ArchivePathResolver Resolver => _resolver ?? throw new InvalidOperationException("Archive members are read before the resolve phase created the resolver");

    private int Workers => Math.Max(1, Parallelism ?? Environment.ProcessorCount);

    /// <summary>
    /// Bounds the archive inputs resolved at once across the whole resolve phase. Each archive resolves its inputs in
    /// parallel while up to <see cref="Workers"/> archives are being prepared, so without a shared limit the two loops multiply.
    /// The outer loop never holds it, so taking it cannot deadlock.
    /// </summary>
    private SemaphoreSlim? _inputResolves;

    private SemaphoreSlim InputResolves => _inputResolves ?? throw new InvalidOperationException("Archive inputs are resolved before the resolve phase created their limiter");

    private async Task<IReadOnlyList<Prepared>> Resolve(Checked check, WriteState state, CancellationToken cancellationToken)
    {
        _resolver = NewResolver(check);
        _inputResolves = new SemaphoreSlim(Workers);
        var executable = check.Placements
            .Where(p => p.Target is Mo2Target.ModFile or Mo2Target.ProfileSetting or Mo2Target.ProfileTweak or Mo2Target.GameRootFile or Mo2Target.Extra)
            .ToArray();
        executable = LeaveOutFinishedDirectives(check, state, executable);
        Progress.Enter(InstallPhase.Resolve, executable.Length, executable.Sum(p => p.Directive.Size));

        var rows = check.Plan.Rows.GroupBy(r => r.Archive.XxHash64).ToDictionary(g => g.Key, g => g.First());
        var prepared = new Prepared[executable.Length];
        var options = new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(Enumerable.Range(0, executable.Length), options, async (i, token) =>
        {
            var placement = executable[i];
            prepared[i] = await Prepare(check, rows, placement, token);
            Progress.Advance(1, placement.Directive.Size);
        });
        await Resolver.FlushDigests(cancellationToken);

        // Later kinds read what earlier ones wrote; within a kind the list's order is kept.
        return prepared.OrderBy(p => WriteOrder.Of(p.Placement.Directive)).ThenBy(p => p.Placement.Index).ToArray();
    }

    private async Task<Prepared> Prepare(Checked check, Dictionary<FileIdentity, ArchivePlanRow> rows, Placement placement, CancellationToken cancellationToken)
    {
        if (placement.Target is Mo2Target.GameRootFile root)
        {
            if (Options.GameFilesNotToReplace.Any(kept => SameFile(kept, root.Path)))
                return new Prepared(placement, null, null, Silent: true);

            // Only a plain copy can be compared with the live file. A patched or re-encoded directive derives from
            // FromArchive too, but its output is never the vanilla file; it goes the normal way and ResolveMember
            // checks the patch's FromHash against the game file source.
            if (placement.Directive is FromArchive { Source.Parts.Count: 0 } vanilla && vanilla.GetType() == typeof(FromArchive)
                && rows.TryGetValue(vanilla.Source.ArchiveXxHash64, out var gameRow)
                && gameRow.Archive.State is GameFileSourceState gameFile
                && SameFile(gameFile.GameFile, root.Path))
            {
                return await PrepareVanillaFile(check, placement, vanilla, gameRow, gameFile, cancellationToken);
            }
        }

        if (placement.Directive is CreateBsa)
            return new Prepared(placement, null, null, Silent: false, WorkEstimate.OfArchive(await ResolveInputMembers(check, rows, placement.Directive, cancellationToken)));

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
            // The held copy lacks only nested archives the list copies whole; the resolver skips those directives alone.
            case var _ when row.HoldsIncompleteCopy:
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
        public ConcurrentDictionary<string, (FileIdentity Store, FileIdentity XxHash64)> Written { get; } = new();

        /// <summary>Where each kind of skip happened, in the order the rows were written; only the thread that writes rows touches it.</summary>
        public Dictionary<SkipKind, List<string>> Skipped { get; } = new();

        private readonly object _pendingLock = new();
        private List<GeneratedFile> _pending = new();
        private long _pendingBytes;

        /// <summary>Held while pending files are handed to the file store, so a flush never starts before an earlier one has finished.</summary>
        public SemaphoreSlim FlushLock { get; } = new(1, 1);

        /// <summary>
        /// Set, under <see cref="FlushLock"/>, when handing files to the file store failed. A failed flush has already
        /// taken files of items that finished, so a later flush would find nothing waiting and let their rows commit.
        /// </summary>
        public ExceptionDispatchInfo? FlushFailure { get; set; }

        /// <summary>The batch of rows being written; the next commit takes it.</summary>
        public IMainTransaction? Tx { get; set; }

        public int BatchItems { get; set; }
        public long BatchBytes { get; set; }
        public bool BatchDirty { get; set; }
        public Stopwatch BatchAge { get; } = new();

        /// <summary>Mod group name to its id: real once committed, a temp id while its transaction is open.</summary>
        public Dictionary<string, LoadoutItemGroupId> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> NewGroups { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per group, the files an earlier run (or this one) already created, as path and hash.</summary>
        public Dictionary<string, GroupFiles> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Queues a generated file for the file store and returns how many are waiting and their bytes.</summary>
        public (int Count, long Bytes) AddPending(GeneratedFile file)
        {
            lock (_pendingLock)
            {
                _pending.Add(file);
                _pendingBytes += (long)file.Size.Value;
                return (_pending.Count, _pendingBytes);
            }
        }

        public List<GeneratedFile> TakePending()
        {
            lock (_pendingLock)
            {
                var taken = _pending;
                _pending = new List<GeneratedFile>();
                _pendingBytes = 0;
                return taken;
            }
        }
    }

    /// <summary>The destinations of the <see cref="SkipKind.KeptOriginal"/> notes an earlier run left, which the resume deletes and so has to write again.</summary>
    private readonly HashSet<string> _keptOriginals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Leaves out the directives an earlier run already wrote the file of, so a resume locates and writes only the rest.
    /// A directive is finished when the group holds a file at its path that the earlier run recorded for the same list
    /// hash, or, for a file written before the hash was recorded, a plain copy of a library file that has that hash. The
    /// file is marked wanted, so the stale-file pass keeps it, and its destination is recorded for merges that read it.
    /// A directive with no file row (one that was skipped) is never finished, so a skip is always retried.
    /// </summary>
    private Placement[] LeaveOutFinishedDirectives(Checked check, WriteState state, Placement[] executable)
    {
        var remaining = new List<Placement>(executable.Length);
        foreach (var placement in executable)
        {
            if (FindFinishedFile(check, state, placement) is { } finished)
            {
                finished.Known.Wanted = true;
                state.Written[Fold(placement.To)] = (finished.Identity, placement.Directive.XxHash64);
            }
            else
            {
                remaining.Add(placement);
            }
        }

        var finishedCount = executable.Length - remaining.Count;
        if (finishedCount > 0)
            Logger.LogInformation("{Count} of {Total} files of '{List}' are already in the loadout and are kept", finishedCount, executable.Length, check.List.Name);
        return remaining.ToArray();
    }

    private (FileIdentity Identity, KnownFile Known)? FindFinishedFile(Checked check, WriteState state, Placement placement)
    {
        if (placement.Group is not { } groupName || placement.Path is not { } path) return null;
        if (!CanBeFinished(check, placement, path) || _keptOriginals.Contains(placement.To.ToString())) return null;
        if (!state.Files.TryGetValue(groupName, out var files)) return null;
        var expected = placement.Directive.XxHash64;

        foreach (var (identity, known) in files.AtPath(path))
        {
            if (known.ListHash is { } recorded ? recorded.Equals(expected) : IsLibraryCopyOf(placement.Directive, identity)) return (identity, known);
        }
        return null;
    }

    /// <summary>
    /// Whether the directive's output is a file row whose bytes depend only on the list. A tool file and a game's INI
    /// baseline leave no such row, and a path-remapped file or a list-edited profile file depends on this machine.
    /// </summary>
    private bool CanBeFinished(Checked check, Placement placement, GamePath path)
    {
        if (placement.Directive is RemappedInlineFile || IsHashUnchecked(placement.Directive)) return false;
        return placement.Target switch
        {
            Mo2Target.ModFile or Mo2Target.GameRootFile => true,
            Mo2Target.ProfileSetting or Mo2Target.ProfileTweak => OwnedIniPath(check, path) is null,
            _ => false,
        };
    }

    /// <summary>
    /// A file an earlier version wrote has no list hash to compare. A plain copy of an archive member is the member's
    /// bytes, so it is the list's file when the library file holding those bytes has the list's hash.
    /// </summary>
    private bool IsLibraryCopyOf(Directive directive, FileIdentity stored)
    {
        if (directive.GetType() != typeof(FromArchive) || !stored.TryXxHash3(out var storeHash)) return false;
        foreach (var file in LibraryFile.FindByHash(Connection.Db, storeHash))
        {
            if (file.XxHash64Identity() is { } known && known.Equals(directive.XxHash64)) return true;
        }
        return false;
    }

    /// <summary>What producing the bytes of one directive came to, for the one thread that writes rows to take in list order.</summary>
    private abstract record Outcome;

    /// <summary>Nothing for the loadout: a file kept as the user has it, or a tool file already written to its folder.</summary>
    private sealed record NothingToRecord : Outcome
    {
        public static readonly NothingToRecord Instance = new();
    }

    private sealed record FileProduced(FileIdentity Hash, Size Size, FileIdentity XxHash64) : Outcome;

    /// <summary>A file that was written, with a note for each way it is not what the list asked for; each note becomes a <see cref="SkipKind.KeptOriginal"/> row.</summary>
    private sealed record FileProducedWithNotes(FileProduced File, IReadOnlyList<string> Notes) : Outcome;

    private sealed record FileSkipped(SkipKind Kind, string Reason) : Outcome;

    private async Task Write(Checked check, LoadoutItemGroupId groupId, AbsolutePath extrasFolder, WriteState state, IReadOnlyList<Prepared> prepared, CancellationToken cancellationToken)
    {
        Progress.Enter(InstallPhase.Write, prepared.Count, prepared.Sum(p => p.Placement.Directive.Size));
        var extras = new ExtrasFolder(extrasFolder);
        var budget = MemoryBudget ?? ByteBudget.ForThisMachine();

        try
        {
            // Kinds run one after the other: a later kind reads what an earlier one wrote.
            for (var start = 0; start < prepared.Count;)
            {
                var kind = WriteOrder.Of(prepared[start].Placement.Directive);
                var end = start + 1;
                while (end < prepared.Count && WriteOrder.Of(prepared[end].Placement.Directive) == kind) end++;

                // Merges read each other's output, so they stay in the list's order.
                var workers = kind == WriteOrder.Merge ? 1 : Workers;
                await WriteRun(check, groupId, state, extras, budget, prepared, start, end, workers, cancellationToken);
                start = end;
            }

            await CommitBatch(state, cancellationToken);
        }
        finally
        {
            state.Tx?.Dispose();
            state.Tx = null;
            await DiscardPending(state);
        }

        await KeepUserTweaksOfEarlierInstalls(state);
        await RemoveStaleFiles(state);
        LogSkipSummary(check, state);
    }

    private const int SkipExamplesLogged = 5;

    private static LogLevel LevelOf(SkipKind kind) => kind.IsByDesign() ? LogLevel.Information : LogLevel.Warning;

    /// <summary>
    /// One line per kind of skip, so a list that skips thousands of files stays readable in the log; each skip also has
    /// its own line. A kind the list leaves out on purpose is Information, any other is a Warning.
    /// </summary>
    private void LogSkipSummary(Checked check, WriteState state)
    {
        foreach (var (kind, paths) in state.Skipped.OrderBy(pair => pair.Key))
        {
            Logger.Log(LevelOf(kind), "{Count} file(s) of '{List}' were skipped as {Kind}, for example: {Examples}",
                paths.Count, check.List.Name, kind, string.Join(", ", paths.Take(SkipExamplesLogged)));
        }
    }

    /// <summary>
    /// Produces the bytes of <paramref name="prepared"/>[<paramref name="start"/>..<paramref name="end"/>] on several
    /// workers, as many as the byte budget lets in, while this thread takes the results in list order and writes the
    /// rows. Producing carries on during a commit, so a slow commit does not hold up the next files.
    /// </summary>
    private async Task WriteRun(Checked check, LoadoutItemGroupId groupId, WriteState state, ExtrasFolder extras, ByteBudget budget,
        IReadOnlyList<Prepared> prepared, int start, int end, int workers, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var slots = new SemaphoreSlim(workers);
        // Octodiff allocates megabytes of buffers for every patch whatever its size, and patching is mostly copying, so
        // it gets few workers; the garbage it leaves is what a patch-heavy list would otherwise pile up.
        using var patchSlots = new SemaphoreSlim(Math.Max(2, workers / 8));
        var results = Channel.CreateBounded<Task<Outcome>>(new BoundedChannelOptions(Math.Max(256, workers * 4)) { SingleReader = true, SingleWriter = true });
        var admitting = Task.Run(() => Admit(check, state, extras, budget, slots, patchSlots, results.Writer, prepared, start, end, stop.Token), CancellationToken.None);

        try
        {
            for (var i = start; i < end; i++)
            {
                var item = prepared[i];
                if (!await results.Reader.WaitToReadAsync(stop.Token)) break;
                if (!results.Reader.TryRead(out var produced))
                    throw new InvalidOperationException("A result was announced but is not in the queue");
                var outcome = await produced;

                Record(check, groupId, state, item, outcome);
                Progress.Advance(1, item.Placement.Directive.Size);
                if (BatchIsDue(state)) await CommitBatch(state, cancellationToken);
            }
            await admitting;
        }
        finally
        {
            // Whatever stopped this run, nothing it started may still be writing when the caller moves on.
            await stop.CancelAsync();
            try { await admitting; } catch { /* the failure being reported is the one that stopped the run */ }
            while (results.Reader.TryRead(out var abandoned))
            {
                try { await abandoned; } catch { /* as above */ }
            }
        }
    }

    private async Task Admit(Checked check, WriteState state, ExtrasFolder extras, ByteBudget budget, SemaphoreSlim slots, SemaphoreSlim patchSlots,
        ChannelWriter<Task<Outcome>> results, IReadOnlyList<Prepared> prepared, int start, int end, CancellationToken cancellationToken)
    {
        try
        {
            for (var i = start; i < end; i++)
            {
                var item = prepared[i];
                Task<Outcome> produced;
                if (NeedsNoWork(item))
                {
                    produced = Execute(check, state, extras, item, cancellationToken);
                }
                else
                {
                    var inputs = InputsOf(check, item.Placement.Directive);
                    var patching = item.Placement.Directive is PatchedFromArchive or MergedPatch || inputs.Any(i => i is PatchedFromArchive or MergedPatch);
                    await slots.WaitAsync(cancellationToken);
                    IDisposable reservation;
                    try
                    {
                        if (patching) await patchSlots.WaitAsync(cancellationToken);
                        try
                        {
                            reservation = await budget.Reserve(
                                item.Placement.Directive is CreateBsa
                                    ? item.ArchiveEstimate ?? throw new InvalidOperationException($"The archive build for {item.Placement.Directive.To} was prepared without a work estimate")
                                    : WorkEstimate.Of(item.Placement.Directive, item.Member),
                                cancellationToken);
                        }
                        catch
                        {
                            if (patching) patchSlots.Release();
                            throw;
                        }
                    }
                    catch
                    {
                        slots.Release();
                        throw;
                    }
                    produced = Task.Run(async () =>
                    {
                        try
                        {
                            return await Execute(check, state, extras, item, cancellationToken);
                        }
                        finally
                        {
                            reservation.Dispose();
                            if (patching) patchSlots.Release();
                            slots.Release();
                        }
                    }, CancellationToken.None);
                }

                try
                {
                    await results.WriteAsync(produced, cancellationToken);
                }
                catch
                {
                    try { await produced; } catch { /* stopping anyway */ }
                    throw;
                }
            }
            results.Complete();
        }
        catch (Exception e)
        {
            results.TryComplete(e);
        }
    }

    /// <summary>
    /// The directives an archive is built from, each with the member it reads, so the build can be sized before it starts.
    /// Done in the resolve phase, off the admit loop; the resolver caches, so the build itself finds these already done. An input whose member cannot be
    /// resolved has none here; the build reports why when it reaches that input.
    /// </summary>
    private async Task<IReadOnlyList<(Directive Directive, ResolvedMember? Member)>> ResolveInputMembers(Checked check, Dictionary<FileIdentity, ArchivePlanRow> rows, Directive directive, CancellationToken cancellationToken)
    {
        var inputs = InputsOf(check, directive).ToArray();
        var resolved = new (Directive Directive, ResolvedMember? Member)[inputs.Length];
        var options = new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(Enumerable.Range(0, inputs.Length), options, async (i, token) =>
        {
            ResolvedMember? member = null;
            if (inputs[i] is FromArchive from)
            {
                await InputResolves.WaitAsync(token);
                try
                {
                    member = await ResolveMember(rows, from, token);
                }
                catch (DirectiveSkipped)
                {
                    // Left without a member; BuildArchive reaches this input and reports the skip with its reason.
                }
                finally
                {
                    InputResolves.Release();
                }
            }
            resolved[i] = (inputs[i], member);
        });
        return resolved;
    }

    /// <summary>The directives whose files an archive is built from; none for any other directive.</summary>
    private static IReadOnlyCollection<Directive> InputsOf(Checked check, Directive directive)
        => directive is CreateBsa bsa && check.BsaInputs.TryGetValue(Fold(bsa.TempId), out var inputs)
            ? inputs.Values.Select(p => p.Directive).ToArray()
            : [];

    /// <summary>A directive whose bytes are already in the file store, or that has none to write.</summary>
    private static bool NeedsNoWork(Prepared item)
        => item.Silent || item.Skip is not null || item.Placement.Directive.GetType() == typeof(FromArchive) && item.Placement.Target is not Mo2Target.Extra;

    private bool BatchIsDue(WriteState state)
        => state.BatchItems >= Commits.MaxItems || state.BatchBytes >= Commits.MaxBytes || state.BatchAge.Elapsed >= Commits.MaxInterval;

    /// <summary>
    /// Stores the generated files the open batch refers to, commits its rows and starts the next batch. Files first, so a
    /// row never names bytes the store lacks.
    /// </summary>
    private async Task CommitBatch(WriteState state, CancellationToken cancellationToken)
    {
        if (state.Tx is not { } tx) return;

        if (state.BatchDirty)
        {
            await FlushPending(state, cancellationToken);
            var result = await tx.Commit();
            foreach (var name in state.NewGroups) state.Groups[name] = result[state.Groups[name]];
            state.NewGroups.Clear();
        }

        tx.Dispose();
        state.Tx = null;
        state.BatchItems = 0;
        state.BatchBytes = 0;
        state.BatchDirty = false;
        state.BatchAge.Reset();
    }

    private IMainTransaction OpenBatch(WriteState state)
    {
        if (state.Tx is { } open) return open;
        state.BatchAge.Restart();
        return state.Tx = Connection.BeginTransaction();
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
        foreach (var known in state.Files.Values.SelectMany(files => files.Known))
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

    /// <summary>
    /// An earlier version installed the list's initweaks.ini as the loadout's own tweak, and the INI editor wrote the
    /// user's tweaks into the list's settings group, so rows at the loadout's tweak paths in that group may hold
    /// the user's edits. They move to the loadout's own settings group instead of being deleted as rows the list did not
    /// ask for. A row whose bytes are still what the list wrote (this run's text, or the xxHash64 the row recorded when
    /// it was written) is redundant next to the list's tweak and is left to be deleted. When the user's group already
    /// has a tweak for the same INI, the row is merged under it, so the user's own keys win, and then deleted.
    /// </summary>
    private async Task KeepUserTweaksOfEarlierInstalls(WriteState state)
    {
        if (!state.Files.TryGetValue(Mo2Layout.ProfileSettingsGroup, out var files)) return;

        var db = Connection.Db;
        var loadout = Loadout.Load(db, TargetLoadout);
        var userRows = UserTweakRows(db, loadout);
        using var tx = Connection.BeginTransaction();
        var pins = new List<IDisposable>();
        try
        {
            LoadoutItemGroupId? userGroup = null;
            var changed = false;
            foreach (var path in files.Paths.ToList())
            {
                if (!IniTweakLayers.IsLoadoutTweakPath(path, out var target)) continue;

                var listPath = IniTweakLayers.TweakPath(Mo2Layout.ListTweakName, target);
                foreach (var (identity, known) in files.AtPath(path))
                {
                    if (known.Wanted || known.Ids.Count == 0 || files.TryGet(listPath, identity, out _)) continue;
                    if (await IsListOutput(known, identity)) continue;

                    if (userRows.TryGetValue(target, out var user))
                    {
                        var under = IniDocument.Parse(await FileStore.Load(identity));
                        under.Merge(IniDocument.Parse(await FileStore.Load(user.Identity)));
                        var bytes = under.ToBytes();
                        pins.Add(await IniEdits.Store(FileStore, path, bytes));
                        var merged = FileIdentity.XxHash3(bytes.xxHash3());
                        tx.Add(user.Id, LoadoutFile.Hash, bytes.xxHash3());
                        tx.Add(user.Id, LoadoutFile.Size, Size.FromLong(bytes.Length));
                        userRows[target] = (user.Id, merged);
                        // The stale-file pass deletes the legacy rows once this commit has folded them in.
                        Logger.LogInformation("Merged the loadout tweak of '{Target}' from an earlier install under the user's own", target);
                    }
                    else
                    {
                        userGroup ??= IniEdits.UserSettingsGroup(db, tx, loadout);
                        tx.Add(known.Ids[0], LoadoutItem.ParentId, userGroup.Value);
                        userRows[target] = (known.Ids[0], identity);
                        // Further rows at this path are duplicates of the first; the stale-file pass deletes them.
                        known.Ids.RemoveAt(0);
                        Logger.LogInformation("Kept the loadout tweak of '{Target}' as the user's: an earlier install had put it in the list's settings group", target);
                    }
                    changed = true;
                }
            }
            if (changed) await tx.Commit();
        }
        finally
        {
            foreach (var pin in pins) pin.Dispose();
        }
    }

    /// <summary>Whether a legacy row still holds what the list wrote: the xxHash64 of its bytes is the one recorded with the row.</summary>
    private async Task<bool> IsListOutput(KnownFile known, FileIdentity identity)
    {
        if (known.ListHash is not { } recorded) return false;
        return FileIdentity.XxHash64(Xx64Hasher.Hash(await FileStore.Load(identity))).Equals(recorded);
    }

    /// <summary>The loadout's own tweak rows in its top-level settings group, by the INI each one changes.</summary>
    private static Dictionary<string, (EntityId Id, FileIdentity Identity)> UserTweakRows(IDb db, Loadout.ReadOnly loadout)
    {
        var rows = new Dictionary<string, (EntityId, FileIdentity)>(StringComparer.OrdinalIgnoreCase);
        if (IniEdits.GameSettingsGroupOf(db, loadout) is not { } group) return rows;

        foreach (var file in LoadoutItem.FindByParent(db, group).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile())
        {
            var path = (GamePath)file.AsLoadoutItemWithTargetPath().TargetPath;
            if (IniTweakLayers.IsLoadoutTweakPath(path, out var target)) rows.TryAdd(target, (file.Id, file.Identity));
        }
        return rows;
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
                var path = file.AsLoadoutItemWithTargetPath().TargetPath;
                if (!files.TryGet(path, file.Identity, out var known)) known = files.Add(path, file.Identity, new KnownFile());
                known.Ids.Add(file.Id);
                if (WabbajackInstalledFile.ListHash.TryGetValue(file, out var listHash)) known.ListHash = listHash;
            }
        }
        return state;
    }

    private async Task FlushPending(WriteState state, CancellationToken cancellationToken)
    {
        await state.FlushLock.WaitAsync(cancellationToken);
        try
        {
            state.FlushFailure?.Throw();
            var batch = state.TakePending();
            if (batch.Count == 0) return;
            try
            {
                await Writer.Store(batch, cancellationToken);
            }
            catch (Exception e)
            {
                state.FlushFailure = ExceptionDispatchInfo.Capture(e);
                throw;
            }
        }
        finally
        {
            state.FlushLock.Release();
        }
    }

    /// <summary>Deletes the temp files of generated files an install that stopped early never stored.</summary>
    private static async Task DiscardPending(WriteState state)
    {
        foreach (var file in state.TakePending()) await file.DisposeAsync();
    }

    /// <summary>
    /// Makes the bytes of one directive and says what they came to. Safe to run for several directives at once: it
    /// writes no rows and touches no group. A directive it cannot execute comes back as <see cref="FileSkipped"/>.
    /// </summary>
    private async Task<Outcome> Execute(Checked check, WriteState state, ExtrasFolder extras, Prepared item, CancellationToken cancellationToken)
    {
        var placement = item.Placement;
        if (item.Silent) return NothingToRecord.Instance;

        try
        {
            if (item.Skip is { } skip) throw new DirectiveSkipped(skip.Kind, skip.Reason);
            if (placement.Target is Mo2Target.Extra extra)
            {
                await WriteExtra(check, extras, item, extra, cancellationToken);
                return NothingToRecord.Instance;
            }
            if (placement.Group is null || placement.Path is null)
                throw new InvalidOperationException($"'{placement.To}' reached the write phase without a place in the loadout");

            var (hash, size, xxHash64, notes) = await Produce(check, state, extras, item, cancellationToken);
            state.Written[Fold(placement.To)] = (hash, xxHash64);
            var produced = new FileProduced(hash, size, xxHash64);
            return notes.Count > 0 ? new FileProducedWithNotes(produced, notes) : produced;
        }
        catch (DirectiveSkipped skipped)
        {
            return new FileSkipped(skipped.Kind, skipped.Message);
        }
        catch (PatchException patchFailed)
        {
            return new FileSkipped(SkipKind.Transform, $"the patch for '{placement.To}' could not be applied: {patchFailed.Message}");
        }
        catch (UnsupportedTextureFormatException unsupported)
        {
            return new FileSkipped(SkipKind.Transform, $"the texture '{placement.To}' could not be re-encoded: {unsupported.Message}");
        }
        catch (BethesdaArchiveException archiveFailed)
        {
            return new FileSkipped(SkipKind.Transform, $"the archive '{placement.To}' could not be built: {archiveFailed.Message}");
        }
        catch (ArchiveMismatchException mismatch)
        {
            return new FileSkipped(SkipKind.Transform, $"the archive '{placement.To}' was built wrong: {mismatch.Message}");
        }
        catch (MemberNotFoundException notFound)
        {
            return new FileSkipped(SkipKind.MemberNotFound, notFound.Message);
        }
    }

    /// <summary>Writes the rows for what <paramref name="outcome"/> says about <paramref name="item"/>. Only the thread that takes results in list order calls this.</summary>
    private void Record(Checked check, LoadoutItemGroupId modlistGroup, WriteState state, Prepared item, Outcome outcome)
    {
        var placement = item.Placement;
        state.BatchItems++;
        state.BatchBytes += placement.Directive.Size;
        if (outcome is NothingToRecord) return;

        var tx = OpenBatch(state);
        state.BatchDirty = true;
        switch (outcome)
        {
            case FileProduced produced:
                RecordFile(tx, check, modlistGroup, state, placement, produced);
                break;
            case FileProducedWithNotes withNotes:
                // The file row goes in first: AddSkip only marks earlier rows wanted, it retracts nothing.
                RecordFile(tx, check, modlistGroup, state, placement, withNotes.File);
                foreach (var note in withNotes.Notes)
                    AddSkip(tx, modlistGroup, state, placement, SkipKind.KeptOriginal, note, check);
                break;
            case FileSkipped skipped:
                AddSkip(tx, modlistGroup, state, placement, skipped.Kind, skipped.Reason, check);
                break;
            default:
                throw new InvalidOperationException($"No rows are written for a {outcome.GetType().Name}");
        }
    }

    private void RecordFile(ITransaction tx, Checked check, LoadoutItemGroupId modlistGroup, WriteState state, Placement placement, FileProduced produced)
    {
        if (placement.Group is not { } groupName || placement.Path is not { } path)
            throw new InvalidOperationException($"'{placement.To}' was produced without a place in the loadout");
        if (placement.Target is Mo2Target.ProfileSetting && OwnedIniPath(check, path) is { } iniPath)
            SetIniBaseline(tx, modlistGroup, state, groupName, check, iniPath, produced.Hash);
        else
            AddFile(tx, modlistGroup, state, groupName, path, produced.Hash, produced.Size, placement.Directive.XxHash64);
    }

    /// <summary>A tool file goes to the list's folder on disk as it is: the loadout never deploys it, so the file store is not involved.</summary>
    private async Task WriteExtra(Checked check, ExtrasFolder extras, Prepared item, Mo2Target.Extra extra, CancellationToken cancellationToken)
    {
        if (item.Placement.Directive is MergedPatch or CreateBsa)
            throw new DirectiveSkipped(SkipKind.ToolFolder, $"'{item.Placement.To}' is built from other files of the list, which a tool folder does not keep");

        var destination = extras.Root.Combine(extra.Path);
        destination.Parent.CreateDirectory();
        await using var output = destination.Create();
        // A tool file has no rows to hold a note, so a kept original there is silent.
        await WriteBytes(check, extras, item.Placement, item.Member, output, parallelism: 1, keepOriginalIfUnsupported: true, notes: [], cancellationToken);
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

        LogSkip(check, placement, kind, reason);
        if (!state.Skipped.TryGetValue(kind, out var skippedOfKind)) state.Skipped[kind] = skippedOfKind = new List<string>();
        skippedOfKind.Add(placement.To.ToString());

        // An archive that could not be built takes its inputs with it: they are not installed anywhere else. A kept
        // original is a note on an archive that was built, so its inputs are all there.
        if (kind != SkipKind.KeptOriginal && placement.Directive is CreateBsa bsa && check.BsaInputs.TryGetValue(Fold(bsa.TempId), out var inputs))
        {
            Progress.AddDropped(inputs.Count);
            Logger.LogWarning("Dropped {Count} input file(s) of '{To}' in '{List}' because the archive was not built ({Reason}): {Inputs}",
                inputs.Count, placement.To, check.List.Name, reason, string.Join(", ", inputs.Values.Take(SkipExamplesLogged).Select(i => i.To.ToString())));
        }
    }

    /// <summary>
    /// Every skip passes through here, so the log names each one with its destination, kind and reason. A file left out
    /// on purpose (a kept original, an archive a tool folder cannot hold) is Information; anything the run failed to
    /// produce is a Warning, since the list stays disabled over it.
    /// </summary>
    private void LogSkip(Checked check, Placement placement, SkipKind kind, string reason)
    {
        Logger.Log(LevelOf(kind), "Skipped '{To}' of '{List}' ({Kind}): {Reason}", placement.To, check.List.Name, kind, reason);
    }

    /// <summary>
    /// A skip means this run could not produce the file, not that the list stopped asking for it, so whatever an earlier
    /// run installed at that path stays until a run can replace it.
    /// </summary>
    private static void KeepEarlierRows(WriteState state, Placement placement)
    {
        if (placement.Group is not { } groupName || placement.Path is not { } path) return;
        if (!state.Files.TryGetValue(groupName, out var files)) return;
        foreach (var (_, known) in files.AtPath(path)) known.Wanted = true;
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

    private void AddFile(ITransaction tx, LoadoutItemGroupId modlistGroup, WriteState state, string groupName, GamePath path, FileIdentity identity, Size size, FileIdentity listHash)
    {
        var expected = listHash;
        if (state.Files.TryGetValue(groupName, out var known) && known.TryGet(path, identity, out var existing))
        {
            existing.Wanted = true;
            // A file an earlier version of the app wrote carries no list hash; recording it lets the next run keep the file without reading anything.
            if (existing.ListHash is null && existing.Ids.Count > 0)
            {
                tx.Add(existing.Ids[0], WabbajackInstalledFile.ListHash, expected);
                existing.ListHash = expected;
            }
            return;
        }

        var group = EnsureGroup(tx, modlistGroup, state, groupName);
        known = state.Files[groupName];

        _ = new WabbajackInstalledFile.New(tx, out var fileId)
        {
            ListHash = expected,
            LoadoutFile = new LoadoutFile.New(tx, fileId)
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
            },
        };
        known.Add(path, identity, new KnownFile { Wanted = true, ListHash = expected });
    }

    #endregion

    #region Tools

    /// <summary>
    /// Registers the executables the list's ModOrganizer.ini names as tools of the installation, once their files are on
    /// disk. A tool that is already registered is left as it is, so installing again adds nothing.
    /// </summary>
    private async Task RegisterTools(Checked check, LoadoutItemGroupId modlistGroup, AbsolutePath extrasFolder, CancellationToken cancellationToken)
    {
        var iniText = await ReadModOrganizerIni(check, cancellationToken);
        if (iniText is null) return;

        var installation = check.Loadout.InstallationInstance;
        var extras = new ExtrasFolder(extrasFolder);
        var gameRoot = installation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, RelativePath.Empty));
        var primary = installation.Game.GetPrimaryFile(installation);

        var skips = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in WabbajackSkippedFile.FindByGroup(Connection.Db, modlistGroup))
            skips[row.To] = row.Reason;
        var skipped = skips.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var switchedOff = await GroupsSwitchedOffByProfile(check, cancellationToken);
        var found = ModlistTools.Find(iniText, extras.Root, gameRoot, primary.FileName.ToString(), relative => AppliedPath(check, relative, skipped, switchedOff),
            relative => skips.GetValueOrDefault(relative), StockGameFolders(check), ListFolders(check));
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

    /// <summary>
    /// Where Apply puts the list's file at <paramref name="relative"/> (a path inside the Mod Organizer instance), for the
    /// files the loadout deploys: game-root folders land in the game folder and mod files at their mapped place. Null for
    /// anything else, including a file this run skipped and a file of a mod the profile's modlist.txt switches off: Apply
    /// never writes either, so a tool pointing at them would not start.
    /// </summary>
    private static string? AppliedPath(Checked check, string relative, IReadOnlySet<string> skipped, IReadOnlySet<string> switchedOff)
    {
        var placement = check.Placements.FirstOrDefault(p =>
            p.Target is Mo2Target.GameRootFile or Mo2Target.ModFile
            && p.Path is not null
            && string.Equals(p.To.ToString(), relative, StringComparison.OrdinalIgnoreCase));
        if (placement?.Path is not { } path) return null;
        if (skipped.Contains(placement.To.ToString()) || placement.Group is { } group && switchedOff.Contains(group)) return null;

        return check.Loadout.InstallationInstance.Locations.ToAbsolutePath(path).ToString();
    }

    /// <summary>The names of the groups the profile's modlist.txt gives a '-' line, which <see cref="Order"/> switches off.</summary>
    private async Task<IReadOnlySet<string>> GroupsSwitchedOffByProfile(Checked check, CancellationToken cancellationToken)
    {
        var texts = await ReadProfileTexts(check, cancellationToken);
        if (texts.ModlistTxt is null) return new HashSet<string>();

        return ModlistTxt.Parse(texts.ModlistTxt)
            .Where(e => !e.IsSeparator && !e.IsForeign && !e.Enabled)
            .Select(e => e.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
        var rootNames = StockGameFolders(check);
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
        // A kept original is on disk and the game loads it, and a tool folder never held the file, so only rows for content that is missing keep the list off.
        if (WabbajackSkippedFile.FindByGroup(db, modlistGroup).All(r => r.Kind.IsByDesign()) && LoadoutItem.Load(db, modlistGroup).IsDisabled)
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
    private async Task<(FileIdentity Identity, Size Size, FileIdentity XxHash64, IReadOnlyList<string> Notes)> Produce(Checked check, WriteState state, ExtrasFolder extras, Prepared item, CancellationToken cancellationToken)
    {
        var directive = item.Placement.Directive;
        switch (directive)
        {
            case FromArchive when directive.GetType() == typeof(FromArchive):
            {
                var member = RequireMember(item.Placement, item.Member);
                return (member.StoreIdentity, member.Size, member.XxHash64 ?? directive.XxHash64, []);
            }

            case MergedPatch merged:
                return WithNotes(await Generated(state, directive, await MergedBytes(check, state, merged, cancellationToken), checkHash: true, cancellationToken), []);

            case CreateBsa bsa:
            {
                // Inputs the archive could not re-encode are stored as they are; the notes say which.
                var archiveNotes = new List<string>();
                return WithNotes(await Generated(state, directive, output => BuildArchive(check, extras, bsa, output, archiveNotes, cancellationToken), checkHash: false, cancellationToken), archiveNotes);
            }

            default:
                var exempt = directive is RemappedInlineFile or TransformedTexture || IsHashUnchecked(directive);
                // Loose files keep the original bytes when the texture cannot be re-encoded; the notes say which did.
                var notes = new List<string>();
                var file = await Generated(state, directive, output => WriteBytes(check, extras, item.Placement, item.Member, output, parallelism: 1, keepOriginalIfUnsupported: true, notes, cancellationToken), checkHash: !exempt, cancellationToken);
                return WithNotes(file, notes);
        }
    }

    private static (FileIdentity, Size, FileIdentity, IReadOnlyList<string>) WithNotes((FileIdentity Identity, Size Size, FileIdentity XxHash64) file, IReadOnlyList<string> notes)
        => (file.Identity, file.Size, file.XxHash64, notes);

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

        var (waiting, waitingBytes) = state.AddPending(file);
        if (waiting >= GeneratedFileWriter.StoreBatchSize || waitingBytes >= Writer.StoreByteLimit) await FlushPending(state, cancellationToken);
        return (file.XxHash3, file.Size, file.XxHash64);
    }

    /// <summary>Recompress writes nothing before it throws, but it has read from the member's stream, so open the member again.</summary>
    private async Task KeepOriginal(Placement placement, ResolvedMember? member, Stream output, CancellationToken cancellationToken)
    {
        await using var original = await RequireMember(placement, member).Open();
        await original.CopyToAsync(output, cancellationToken);
    }

    /// <summary>
    /// The bytes of one directive other than a merge or an archive, written to <paramref name="output"/>. A texture whose
    /// format cannot be re-encoded is copied as it is when <paramref name="keepOriginalIfUnsupported"/> allows it, and
    /// <paramref name="notes"/> gets a line saying so; otherwise the exception goes to the caller. <paramref name="named"/>
    /// is the path that line uses when the directive's own path is a temp folder name, as it is for an archive's input.
    /// </summary>
    private async Task WriteBytes(Checked check, ExtrasFolder extras, Placement placement, ResolvedMember? member, Stream output,
        int parallelism, bool keepOriginalIfUnsupported, List<string> notes, CancellationToken cancellationToken, RelativePath? named = null)
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
                try
                {
                    await using var dds = await RequireMember(placement, member).Open();
                    await Transforms.Recompress(dds, texture.Image, output, parallelism, cancellationToken);
                }
                catch (UnreadableTextureException unreadable) when (keepOriginalIfUnsupported)
                {
                    await KeepOriginal(placement, member, output, cancellationToken);
                    notes.Add($"kept the original '{named ?? placement.To}' because it could not be read as a texture: {unreadable.Message}");
                }
                catch (UnsupportedTextureFormatException unsupported) when (keepOriginalIfUnsupported)
                {
                    await KeepOriginal(placement, member, output, cancellationToken);
                    notes.Add($"kept the original '{named ?? placement.To}' because its format cannot be re-encoded to {texture.Image.Format}: {unsupported.Message}");
                }
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

    private readonly Lock _markerLock = new();
    private PathMarkerValues? _markerValues;

    /// <summary>The values that replace path markers; the same for every file of an install, so worked out once.</summary>
    private PathMarkerValues MarkerValues(Checked check, ExtrasFolder extras)
    {
        lock (_markerLock) return _markerValues ??= WorkOutMarkerValues(check, extras);
    }

    private PathMarkerValues WorkOutMarkerValues(Checked check, ExtrasFolder extras)
    {
        var installation = check.Loadout.InstallationInstance;
        var gameRoot = installation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, RelativePath.Empty));

        // Without a prefix the translator leaves a Z: path, which Wine maps to the host root.
        var resolution = PrefixResolver.Resolve(installation);
        var prefixRoot = resolution.Prefix?.Path.ToString() ?? "";
        return new PathMarkerValues(
            GamePath: WindowsPathTranslator.Translate(gameRoot.ToString(), prefixRoot),
            Mo2Path: WindowsPathTranslator.Translate(extras.Root.ToString(), prefixRoot),
            DownloadsPath: WindowsPathTranslator.Translate(extras.Downloads.ToString(), prefixRoot),
            StockGameFolders: StockGameFolders(check),
            ListFolders: ListFolders(check));
    }

    /// <summary>The list's stock-game folders, which Apply deploys into the game folder: the ones <see cref="Mo2Layout.Classify"/> sorted files into.</summary>
    private static HashSet<string> StockGameFolders(Checked check)
        => check.Placements.Select(p => p.Target).OfType<Mo2Target.GameRootFile>()
            .Select(t => t.GroupName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The top-level folders the list's directives write into.</summary>
    private static HashSet<string> ListFolders(Checked check)
        => check.Placements.Select(p => p.To.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length > 1)
            .Select(parts => parts[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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

    private async Task BuildArchive(Checked check, ExtrasFolder extras, CreateBsa bsa, Stream output, List<string> notes, CancellationToken cancellationToken)
    {
        var inputs = check.BsaInputs.GetValueOrDefault(Fold(bsa.TempId)) ?? new Dictionary<string, Placement>();
        var rows = check.Plan.Rows.GroupBy(r => r.Archive.XxHash64).ToDictionary(g => g.Key, g => g.First());
        var spilled = new List<TemporaryPath>();
        try
        {
            async Task<Stream> OpenInput(RelativePath path, FileState fileState)
            {
                if (!inputs.TryGetValue(Fold(path), out var input))
                    throw new DirectiveSkipped(SkipKind.MemberNotFound, $"the archive '{bsa.To}' needs '{path}', which no directive writes");

                var member = input.Directive is FromArchive from ? await ResolveMember(rows, from, cancellationToken) : null;
                TemporaryPath temporary;
                // A BA2 texture's state records its size, mips and format, so an original that was not re-encoded would not match it.
                var canKeepOriginal = fileState is not Ba2Dx10FileState;
                try
                {
                    temporary = await Writer.WriteTemporary(file => WriteBytes(check, extras, input, member, file, Workers, canKeepOriginal, notes, cancellationToken, named: path));
                }
                catch (UnsupportedTextureFormatException unsupported)
                {
                    // The member's own path says which texture failed; the input's directive path is a temp folder name.
                    var format = input.Directive is TransformedTexture texture ? texture.Image.Format : "unknown";
                    throw new DirectiveSkipped(SkipKind.Transform, $"the archive '{bsa.To}' could not be built: the texture '{path}' in it could not be re-encoded to {format} and its {nameof(Ba2Dx10FileState)} records the size and format, so the original cannot stand in: {unsupported.Message}");
                }
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
