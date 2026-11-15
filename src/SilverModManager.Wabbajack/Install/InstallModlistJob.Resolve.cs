using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Channels;
using SilverModManager.Sdk.Diagnostics;
using SilverModManager.Sdk.Hashes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Games.Modlists;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.BuiltInEntities;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.ToolRunner;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Library;
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

public sealed partial class InstallModlistJob
{
    private sealed record Skip(SkipKind Kind, string Reason);

    /// <param name="ArchiveEstimate">For an archive build, its <see cref="WorkEstimate.OfArchive"/> size worked out from the members its inputs read, so Admit can reserve for it without reading anything and the members themselves are not kept.</param>
    private sealed record Prepared(Placement Placement, ResolvedMember? Member, Skip? Skip, bool Silent, long? ArchiveEstimate = null);

    private sealed class DirectiveSkipped(SkipKind kind, string reason) : Exception(reason)
    {
        public SkipKind Kind { get; } = kind;
    }

    private ArchivePathResolver NewResolver(Checked check)
        => new(check.Plan, Connection.Db, FileStore, check.GameFiles, Openers, TemporaryFiles, Logger);

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

    /// <summary>The placements the install executes: the ones that become a file in the loadout or an extra.</summary>
    internal static Placement[] Executable(IReadOnlyList<Placement> placements)
        => placements
            .Where(p => p.Target is Mo2Target.ModFile or Mo2Target.ProfileSetting or Mo2Target.ProfileTweak or Mo2Target.GameRootFile or Mo2Target.Extra)
            .ToArray();

    /// <summary>
    /// The mod folders the install will fill, one per group, for the rows the page shows. A directive with no group (a
    /// tool file that is never deployed) makes no row and counts in no folder. <paramref name="thisRun"/> is what is
    /// left to write; the rest of <paramref name="allExecutable"/> an earlier run already finished.
    /// </summary>
    internal static IReadOnlyList<ModFolderPlan> FolderPlans(IReadOnlyList<Placement> allExecutable, IReadOnlyList<Placement> thisRun)
    {
        var remaining = new Dictionary<string, int>();
        foreach (var placement in thisRun)
        {
            if (placement.Group is { } group) remaining[group] = remaining.GetValueOrDefault(group) + 1;
        }

        var byFolder = new Dictionary<string, List<Placement>>();
        foreach (var placement in allExecutable)
        {
            if (placement.Group is not { } group) continue;
            if (!byFolder.TryGetValue(group, out var members)) byFolder[group] = members = [];
            members.Add(placement);
        }

        return byFolder
            .Select(f => new ModFolderPlan(
                f.Key, f.Value.Min(p => p.Index), f.Value.Count,
                AlreadyDone: f.Value.Count - remaining.GetValueOrDefault(f.Key),
                LaterPasses: f.Value.Count(p => WriteOrder.Of(p.Directive) != 0),
                MergePasses: f.Value.Count(p => p.Directive is MergedPatch)))
            .OrderBy(f => f.Index)
            .ToArray();
    }

    private async Task<IReadOnlyList<Prepared>> Resolve(Checked check, WriteState state, CancellationToken cancellationToken)
    {
        _resolver = NewResolver(check);
        _inputResolves = new SemaphoreSlim(Workers);
        var allExecutable = Executable(check.Placements);
        var executable = LeaveOutFinishedDirectives(check, state, allExecutable);
        Progress.SetMods(FolderPlans(allExecutable, executable));
        Progress.Enter(InstallPhase.Resolve, executable.Length, executable.Sum(p => p.Directive.Size));

        var rows = check.Plan.Rows.GroupBy(r => r.Archive.XxHash64).ToDictionary(g => g.Key, g => g.First());
        var prepared = new Prepared[executable.Length];
        var options = new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(Enumerable.Range(0, executable.Length), options, async (i, token) =>
        {
            var placement = executable[i];
            var started = Stopwatch.GetTimestamp();
            prepared[i] = await Prepare(check, rows, placement, token);
            var took = Stopwatch.GetElapsedTime(started);
            if (took > SlowDirective) Logger.LogDebug("Locating {Kind} for {To} took {Seconds:F1} s", placement.Directive.GetType().Name, placement.Directive.To, took.TotalSeconds);
            Progress.Advance(placement.Group, 1, placement.Directive.Size);
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
            if (IsPlainGameFileCopy(placement, root, hash => rows.GetValueOrDefault(hash)?.Archive, out var vanilla, out var gameFile))
            {
                return await PrepareVanillaFile(check, placement, vanilla, rows[vanilla.Source.ArchiveXxHash64], gameFile, cancellationToken);
            }

            // After the vanilla comparison: a vanilla copy that matched leaves no row and no note either, and it still
            // has to be compared again, since a game update can make it differ.
            if (_gameFilesKeptByUser.Any(kept => SameFile(kept, root.Path)))
                return new Prepared(placement, null, null, Silent: true);
        }

        if (placement.Directive is CreateBsa)
            return new Prepared(placement, null, null, Silent: false, WorkEstimate.OfArchive(await ResolveInputMembers(check, rows, placement.Directive, cancellationToken)));

        if (placement.Directive is not FromArchive from) return new Prepared(placement, null, null, Silent: false);

        // The library never unpacks these, so the member would not be found. They are left out without a note: a list can
        // carry hundreds of them (a mod's whole src/ folder), and nothing the user could do about one is missing.
        if (NamesAFileNoGameLoads(from)) return new Prepared(placement, null, null, Silent: true);

        try
        {
            return new Prepared(placement, await ResolveMember(rows, from, cancellationToken), null, Silent: false);
        }
        catch (DirectiveSkipped skipped)
        {
            return new Prepared(placement, null, new Skip(skipped.Kind, skipped.Message), Silent: false);
        }
    }

    /// <summary>Whether the directive reads, or writes, a file of the kind <see cref="NeverLoadedFiles"/> describes.</summary>
    private static bool NamesAFileNoGameLoads(FromArchive directive)
        => NeverLoadedFiles.IsNeverLoaded(directive.To) || (directive.Source.Parts.Count > 0 && NeverLoadedFiles.IsNeverLoaded(directive.Source.Parts[^1]));

    /// <summary>
    /// Whether the placement is a plain copy of the game's own file at the same path, which the install compares with the
    /// live file instead of writing. <paramref name="archiveOf"/> finds the list's archive entry by hash, so the question
    /// can be asked of any version of a list, not only the one whose archives were matched against the library.
    /// </summary>
    internal static bool IsPlainGameFileCopy(
        Placement placement, Mo2Target.GameRootFile root, Func<FileIdentity, ArchiveEntry?> archiveOf,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FromArchive? vanilla,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GameFileSourceState? gameFile)
    {
        vanilla = null;
        gameFile = null;
        if (placement.Directive is not FromArchive { Source.Parts.Count: 0 } copy || copy.GetType() != typeof(FromArchive)) return false;
        if (archiveOf(copy.Source.ArchiveXxHash64)?.State is not GameFileSourceState source) return false;
        if (!SameFile(source.GameFile, root.Path)) return false;

        vanilla = copy;
        gameFile = source;
        return true;
    }

    /// <summary>
    /// The game-folder files the user chose to keep: the ones <paramref name="placements"/> copy in that the earlier
    /// version of the list (<paramref name="earlierPlacements"/>, <paramref name="earlierList"/>) also placed as something
    /// other than a plain copy of the game's file, and that the group holds neither a row nor a skip note for. A file the
    /// earlier version copied from the game and that matched also left neither, so it is not a choice; nor is a file that
    /// only this version places. The install and the update preview both ask here.
    /// </summary>
    internal static IReadOnlySet<RelativePath> GameFilesKept(
        WriteState state, IReadOnlyList<Placement> placements, IReadOnlySet<string> skippedDestinations,
        IReadOnlyList<Placement> earlierPlacements, ModList earlierList)
    {
        var archives = earlierList.Archives.GroupBy(a => a.XxHash64).ToDictionary(g => g.Key, g => g.First());
        var placedBefore = earlierPlacements
            .Where(p => p.Target is Mo2Target.GameRootFile root
                        && !IsPlainGameFileCopy(p, root, hash => archives.GetValueOrDefault(hash), out _, out _))
            .Select(p => Fold(((Mo2Target.GameRootFile)p.Target).Path))
            .ToHashSet(StringComparer.Ordinal);
        var candidates = placements.Where(p => p.Target is Mo2Target.GameRootFile root && placedBefore.Contains(Fold(root.Path))).ToArray();
        return InstallReconciler.GameFilesKeptByUser(state, candidates, skippedDestinations);
    }

    /// <summary>A file the list copies from the game itself: nothing to write when the user's copy is the one the list wants.</summary>
    private static async Task<Prepared> PrepareVanillaFile(Checked check, Placement placement, FromArchive directive, ArchivePlanRow row, GameFileSourceState gameFile, CancellationToken cancellationToken)
    {
        var described = await check.GameFiles.Describe(gameFile.GameFile, cancellationToken);
        if (described is { } mine && mine.XxHash64.Equals(directive.XxHash64))
            return new Prepared(placement, null, null, Silent: true);

        var reason = row.Reason ?? $"'{gameFile.GameFile}' differs from the list's copy (game version {gameFile.GameVersion})";
        // Only a file the game has can stand in for the list's copy; with none, the list still lacks the file.
        var kind = described is null ? SkipKind.GameFileVersion : SkipKind.GameFileOwnVersion;
        return new Prepared(placement, null, new Skip(kind, reason), Silent: false);
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
}
