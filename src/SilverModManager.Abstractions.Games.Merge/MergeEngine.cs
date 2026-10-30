using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.Merge;

/// <summary>
/// The merge engine for any game that describes itself with an <see cref="AMergeGame"/>. Each contested path is folded
/// by the game's strategy over every contributor, highest load-order rank first. The game's own copy is the ancestor
/// only when <see cref="IVanillaTrust"/> vouches for it. Nothing here refuses an apply: a path that cannot be merged is
/// reported not mergeable and the load order decides it.
/// <para>
/// Re-entrancy: <see cref="PlannedOutputs"/> and the planning that feeds the cache read loadout rows through model
/// accessors only (see <see cref="MergeContributors"/>), so the synchronizer can call them from inside its
/// winning-files query or before it has a sync tree.
/// </para>
/// </summary>
public sealed class MergeEngine : IMergeEngine
{
    private const string NotMerged = "not a file the app merges; load order decides";
    private const string GamesOwnCopy = "the game's own copy";

    /// <summary>What became of one contested path: a merged file, or the reason it is left to load order.</summary>
    private sealed record PathResult(ContestedPath Path, MergedOutput? Output, string? NotMergedReason);

    private sealed record Entry(string Key, IReadOnlyList<MergedOutput> Outputs, IReadOnlyList<PathResult> Results);

    /// <summary>
    /// The ancestor of one merge: its bytes when trusted and present, the note that says why not otherwise, and what the
    /// cache key records of it. A trusted copy whose hash came from its fingerprint has no bytes until a merge needs them.
    /// </summary>
    private sealed record VanillaBase(byte[]? Bytes, IMergeNote? Note, string Fingerprint, string? Failure = null)
    {
        public bool TrustedWithoutBytes => Bytes is null && Note is null && Failure is null;
    }

    /// <summary>The hash of an original copy as last read, which stands until its size or modified time moves.</summary>
    private sealed record FileFingerprint(Size Size, DateTime Modified, MultiHash Hashes);

    private readonly IFileStore _fileStore;
    private readonly IVanillaTrust _trust;
    private readonly ILogger<MergeEngine> _logger;
    private readonly Func<Loadout.ReadOnly, AMergeGame> _gameOf;

    private readonly Dictionary<LoadoutId, Entry> _latest = [];
    private readonly object _gate = new();

    // Under _gate. Every plan asks after every contested original copy, and reading and hashing each of them on every
    // refresh is most of what a plan costs once the merges themselves are cached.
    private readonly Dictionary<AbsolutePath, FileFingerprint> _fingerprints = [];
    private readonly ConcurrentDictionary<(LoadoutId, string), byte> _unprioritised = new();

    // Kept apart from the entries because PlannedOutputs asks for it on every refresh and must never wait behind a plan
    // that is being made.
    private readonly ConcurrentDictionary<LoadoutId, IReadOnlySet<string>> _failed = new();

    public MergeEngine(IFileStore fileStore, IVanillaTrust trust, ILogger<MergeEngine> logger)
        : this(fileStore, trust, logger, loadout => ((IHasMergeGame)loadout.InstallationInstance.Game).MergeGame)
    {
    }

    /// <summary>For tests, which have no game class that implements <see cref="IHasMergeGame"/>.</summary>
    internal MergeEngine(IFileStore fileStore, IVanillaTrust trust, ILogger<MergeEngine> logger, Func<Loadout.ReadOnly, AMergeGame> gameOf)
    {
        _fileStore = fileStore;
        _trust = trust;
        _logger = logger;
        _gameOf = gameOf;
    }

    public bool Serves(IGameData game) => game is IHasMergeGame;

    public IReadOnlyList<MergedOutput> Plan(Loadout.ReadOnly loadout, MergeSourceMode mode) => Current(loadout).Outputs;

    public MergePlanReport PlanAndReport(Loadout.ReadOnly loadout, MergeSourceMode mode)
    {
        var entry = Current(loadout);
        return new MergePlanReport(ReportOf(_gameOf(loadout), entry), entry.Outputs);
    }

    public byte[]? MergedBytes(Loadout.ReadOnly loadout, string relPath)
        => Plan(loadout, MergeSourceMode.Apply).FirstOrDefault(o => o.RelPath.Equals(relPath, StringComparison.OrdinalIgnoreCase))?.Bytes;

    public IReadOnlyList<PlannedOutput> PlannedOutputs(Loadout.ReadOnly loadout)
    {
        var game = _gameOf(loadout);
        var failed = _failed.TryGetValue(loadout.LoadoutId, out var paths) ? paths : EmptyPaths;
        return Contested(loadout, game)
            .Where(p => p.Strategy is not null && !failed.Contains(p.Rel))
            .Select(p => new PlannedOutput(game.OutputFor(p.Rel), p.Rel))
            .ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<ContestedPath> NotContested(Loadout.ReadOnly loadout) => MergeContributors.NotContested(loadout, _gameOf(loadout), _logger);

    public MergeReport Report(Loadout.ReadOnly loadout, MergeSourceMode mode) => ReportOf(_gameOf(loadout), Current(loadout));

    private static MergeReport ReportOf(AMergeGame game, Entry entry)
        => new(entry.Results.Select(Describe).ToArray(), null,
            $"Contributors are ordered by the loadout's load order; on the {game.ReorderHint} page, the mod loaded last wins");

    private static MergeReportEntry Describe(PathResult result)
    {
        var contributors = result.Path.Contributors.Select(c => c.ModName).ToArray();
        if (result.Output is not { } output)
            return new MergeReportEntry(result.Path.Rel, result.Path.Strategy?.Name ?? "", contributors, MergeReportOutcome.NotMergeable, [], "",
                result.NotMergedReason ?? throw new InvalidOperationException($"`{result.Path.Rel}` has neither a merged output nor a reason it wasn't merged"));

        var source = output.Notes.OfType<IVanillaSourceNote>().FirstOrDefault()?.Describe() ?? GamesOwnCopy;
        var notes = output.Notes.Where(n => n is not IVanillaSourceNote).Select(n => n.Describe()).ToArray();
        return new MergeReportEntry(result.Path.Rel, output.Strategy, output.Contributors, MergeReportOutcome.Merged, notes, source, "");
    }

    private IReadOnlyList<ContestedPath> Contested(Loadout.ReadOnly loadout, AMergeGame game)
    {
        var contested = MergeContributors.Of(loadout, game, _logger);
        foreach (var file in contested.SelectMany(p => p.Contributors).Where(f => !f.Prioritised))
        {
            if (_unprioritised.TryAdd((loadout.LoadoutId, file.Package), 0))
                _logger.LogWarning("Package {Package} is in no mod with a priority, so it ranks below every other mod when its files are merged", file.Package);
        }
        return contested;
    }

    private static readonly IReadOnlySet<string> EmptyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private Entry Current(Loadout.ReadOnly loadout)
    {
        var game = _gameOf(loadout);
        var installation = loadout.InstallationInstance;
        var contested = Contested(loadout, game);

        lock (_gate)
        {
            // The key needs each vanilla copy's hash, and trust is asked on every call because a baseline rebuild changes
            // the answer without touching the file; the merging is what the cache saves.
            var vanillas = contested.Select(p => p.Strategy is null ? null : VanillaFor(game, installation, p.Rel, withBytes: false)).ToArray();
            var key = KeyOf(contested, vanillas);
            if (_latest.TryGetValue(loadout.LoadoutId, out var cached) && cached.Key == key) return cached;

            // A merge runs on bytes read now and judged again, so its base is exactly what trust vouched for.
            vanillas = vanillas.Select((v, i) => v is { TrustedWithoutBytes: true } ? VanillaFor(game, installation, contested[i].Rel, withBytes: true) : v).ToArray();
            key = KeyOf(contested, vanillas);

            var results = contested.Select((p, i) => MergePath(game, p, vanillas[i])).ToArray();
            var entry = new Entry(key, results.Select(r => r.Output).OfType<MergedOutput>().ToArray(), results);
            _latest[loadout.LoadoutId] = entry;
            _failed[loadout.LoadoutId] = results.Where(r => r.Output is null && r.Path.Strategy is not null).Select(r => r.Path.Rel).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return entry;
        }
    }

    private static string KeyOf(IReadOnlyList<ContestedPath> contested, VanillaBase?[] vanillas)
        => string.Concat(contested.Select((p, i) => KeyOf(p, vanillas[i])));

    private static string KeyOf(ContestedPath path, VanillaBase? vanilla)
    {
        var contributors = string.Concat(path.Contributors.Select(c => $"c:{c.Package}/{c.Rel}={c.Hash}@{c.Rank} {c.ModName}|"));
        return $"{path.Rel}|{path.Strategy?.Name}|{contributors}v:{vanilla?.Fingerprint}\n";
    }

    /// <summary>
    /// The ancestor of <paramref name="rel"/>. Without <paramref name="withBytes"/> an unchanged fingerprint stands in
    /// for reading the file, and a trusted copy comes back without its bytes.
    /// </summary>
    private VanillaBase VanillaFor(AMergeGame game, GameInstallation installation, string rel, bool withBytes)
    {
        if (game.VanillaFor(rel, installation) is not { } vanillaPath) return new VanillaBase(null, new NoVanillaBase(rel), "none");

        var file = installation.Locations[vanillaPath.LocationId].Path.Combine(vanillaPath.Path);
        if (!file.FileExists) return new VanillaBase(null, new NoVanillaBase(rel), "none");

        try
        {
            var info = file.FileInfo;
            var (size, modified) = (info.Size, info.LastWriteTimeUtc);
            byte[]? bytes = null;
            MultiHash hashes;
            if (!withBytes && _fingerprints.TryGetValue(file, out var known) && known.Size == size && known.Modified == modified)
            {
                hashes = known.Hashes;
            }
            else
            {
                // Read synchronously: planning is a pure function over byte sources and runs where nothing awaits.
                using var stream = file.Read();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
                // Every digest, because the baseline may have recorded the file under any of them.
                hashes = MultiHasher.HashStream(new MemoryStream(bytes)).GetAwaiter().GetResult();
                _fingerprints[file] = new FileFingerprint(size, modified, hashes);
            }

            // Trust is judged on the hash of the bytes on disk, so a copy edited after the baseline was built cannot pass.
            if (!_trust.IsTrusted(installation, vanillaPath, hashes, out var reason))
                return new VanillaBase(null, new VanillaBaseUntrusted(rel, reason), $"untrusted:{reason}");

            return new VanillaBase(bytes, null, hashes.XxHash3.ToString());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new VanillaBase(null, null, $"unreadable:{exception.Message}", $"the game's own copy could not be read: {exception.Message}");
        }
    }

    private PathResult MergePath(AMergeGame game, ContestedPath path, VanillaBase? vanilla)
    {
        if (path.Strategy is not { } strategy) return new PathResult(path, null, NotMerged);
        if (vanilla is null) throw new InvalidOperationException($"No vanilla base was looked up for `{path.Rel}`, which has a merge strategy");
        if (vanilla.Failure is { } unreadable) return Failed(path, unreadable);

        try
        {
            // IFileStore.Load is async, but the planner is synchronous (the synchronizer's preflight and its file
            // writers call it where nothing awaits), the same blocking read LoadoutMergeSources.ContributorBytes makes.
            var inputs = path.Contributors.Select(c => new MergeInput(_fileStore.Load(FileIdentity.XxHash3(c.Hash)).GetAwaiter().GetResult(), c.ModName)).ToArray();
            var notes = new List<IMergeNote>();
            if (vanilla.Note is { } source) notes.Add(source);

            var result = strategy.Fold(inputs, vanilla.Bytes ?? [], vanilla.Bytes is not null, path.Rel, notes);
            _logger.LogTrace("Merged {Path} from {Contributors} contributors in {Hunks} hunks", path.Rel, inputs.Length, result.Hunks);

            var output = new MergedOutput(game.OutputFor(path.Rel), path.Rel, result.Bytes, path.Contributors.Select(c => c.ModName).ToArray(), notes, strategy.Name);
            return new PathResult(path, output, null);
        }
        catch (Exception exception) when (exception is MergeInputException or MissingArchiveException or IOException or UnauthorizedAccessException)
        {
            return Failed(path, exception.Message);
        }
    }

    private PathResult Failed(ContestedPath path, string message)
    {
        _logger.LogWarning("{Path} could not be merged, so the highest-ranked mod's copy is used: {Message}", path.Rel, message);
        return new PathResult(path, null, $"the merge failed: {message}");
    }
}
