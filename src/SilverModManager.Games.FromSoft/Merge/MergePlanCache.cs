using System.Runtime.ExceptionServices;
using System.Text;
using NexusMods.Games.FromSoft.Build;
using NexusMods.Games.FromSoft.Formats;
using NexusMods.Games.FromSoft.Heal;
using NexusMods.Hashing.xxHash3;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Merge;

/// <summary>
/// Keeps the last merge plan of each loadout, so the preflight, the file writers and the reports ask once and get the
/// same bytes. A plan is reused only when everything it was made from is unchanged: the candidates with every
/// contributor's hash, the game's own file for the paths that start from it, and the vanilla file. A loadout change
/// or a game patch therefore merges again, and an unchanged loadout costs a read of the two inputs that live outside
/// the loadout and no merging.
/// <para>
/// Re-entrancy: the index is built from model accessors only, see <see cref="LoadoutMergeSources"/>.
/// </para>
/// </summary>
public sealed class MergePlanCache(LoadoutMergeSources sources, BuildStampStore stamps)
{
    // A plan made in Report mode that would have kept a baseline is not what an apply needs: the apply keeps it.
    private sealed record Entry(string Key, MergePlan Plan, bool ApplyWouldKeepSomething);

    private readonly Dictionary<LoadoutId, Entry> _latest = [];
    private readonly object _gate = new();

    // Identifying the installed build decrypts the regulation, so it is remembered for as long as the game's file and
    // Steam's build id are the ones it was read from.
    private readonly Dictionary<GameInstallMetadataId, (string Key, BuildIdentity Live)> _live = [];

    /// <summary>
    /// The plan for the loadout as it is now. <see cref="MergeSourceMode.Apply"/> may keep a vanilla regulation under
    /// the data root; <see cref="MergeSourceMode.Report"/> writes nothing and says what the next apply would keep.
    /// </summary>
    /// <exception cref="Exception">The index could not be built or a file could not be read for a reason that is not about the merge itself; a merge that can't be made is a <see cref="MergePlan.Refusal"/>.</exception>
    public MergePlan Current(Loadout.ReadOnly loadout, MergeSourceMode mode)
    {
        var installation = loadout.InstallationInstance;
        var traits = ((IFromSoftGame)installation.Game).Formats;

        lock (_gate)
        {
            var index = sources.Index(loadout);
            var inputs = Fetch(loadout, index, mode, traits);
            var key = KeyOf(index, inputs);

            if (_latest.TryGetValue(loadout.LoadoutId, out var known)
                && known.Key == key
                && (mode == MergeSourceMode.Report || !known.ApplyWouldKeepSomething))
                return known.Plan;

            var rebase = RegulationRebase.For(c => sources.VersionOf(installation, c), () => inputs.Live(), traits);
            var plan = MergePlanner.Plan(index, sources.ContributorBytes, (path, _) => inputs.Vanilla(path), inputs.GameBase, traits, MergeStrategyBindings.For, rebase);
            plan = inputs.WithSourceNotes(plan);
            _latest[loadout.LoadoutId] = new Entry(key, plan, inputs.Notes.Values.Any(notes => notes.OfType<BaselineWillBeKept>().Any()));
            return plan;
        }
    }

    // Both inputs are read before the planner runs because they are part of the cache key, and a failure to read one
    // belongs to the path that needed it: it is held and thrown when the planner asks, so it becomes that path's refusal.
    private Inputs Fetch(Loadout.ReadOnly loadout, MergePathIndex index, MergeSourceMode mode, FromSoftFormatTraits traits)
    {
        var inputs = new Inputs();
        foreach (var candidate in index.Candidates.Where(c => c.Outcome.Merges))
        {
            var strategy = candidate.Strategy
                           ?? throw new InvalidOperationException($"{candidate.Path} is marked as merging but has no strategy");
            // The game's file is read first: a regulation that changed without a patch must stop the merge before a
            // vanilla is looked up, since an apply may keep the installed file as the baseline of its version.
            string? refusal = null;
            if (MergeStrategies.TakesGameBase(strategy))
            {
                var gameBase = Read(() => sources.GameBase(loadout.InstallationInstance, candidate.Path));
                inputs.GameBaseOf[candidate.Path] = gameBase;
                if (gameBase.Failure is null)
                {
                    var fingerprint = gameBase.Fingerprint;
                    inputs.Live = () => Identify(loadout, fingerprint);
                    refusal = TamperedReason(loadout, candidate.Path, inputs, traits);
                }
            }
            if (MergeStrategies.NeedsVanilla(strategy))
            {
                var notes = new List<IMergeNote>();
                inputs.Notes[candidate.Path] = notes;
                inputs.VanillaOf[candidate.Path] = refusal is not null
                    ? new Fetched(null, "refused: " + refusal, new MergeException(refusal))
                    : Read(() => sources.Vanilla(loadout, candidate.Path, candidate.Contributors, mode, notes));
            }
        }
        return inputs;
    }

    // A stamp that says the game's data moved while the install did not means something wrote to it. Without a stamp
    // there is nothing to compare with, so the build is not even identified. With one, a build that can't be read
    // refuses too: the check that it was not tampered with could not be made.
    private string? TamperedReason(Loadout.ReadOnly loadout, string path, Inputs inputs, FromSoftFormatTraits traits)
    {
        var stamped = stamps.Find(loadout.InstallationId);
        inputs.Stamps[path] = $"{stamped?.ToString() ?? "none"}/{loadout.InstallationInstance.LocatorResult.StoreBuildId}";
        if (stamped is null) return null;

        BuildIdentity identity;
        try
        {
            identity = inputs.Live();
        }
        catch (MergeException exception)
        {
            return exception.Message;
        }
        return BuildIdentity.Classify(stamped, identity) == BuildChangeKind.Tampered
            ? HealPlan.TamperedRefusal(identity, traits)
            : null;
    }

    private BuildIdentity Identify(Loadout.ReadOnly loadout, string gameFingerprint)
    {
        var installation = loadout.InstallationInstance;
        var key = $"{installation.LocatorResult.StoreBuildId}|{gameFingerprint}";
        if (_live.TryGetValue(loadout.InstallationId, out var known) && known.Key == key) return known.Live;

        try
        {
            var identity = BuildIdentity.Identify(installation);
            _live[loadout.InstallationId] = (key, identity);
            return identity;
        }
        catch (InvalidOperationException exception)
        {
            throw new MergeException($"the installed build could not be identified: {exception.Message}");
        }
    }

    private static Fetched Read(Func<byte[]?> read)
    {
        try
        {
            var bytes = read();
            return new Fetched(bytes, bytes?.xxHash3().ToString() ?? "none", null);
        }
        catch (MergeException exception)
        {
            return new Fetched(null, "refused: " + exception.Message, exception);
        }
    }

    private static string KeyOf(MergePathIndex index, Inputs inputs)
    {
        var key = new StringBuilder();
        foreach (var candidate in index.Candidates)
        {
            key.Append(candidate.Path).Append('|').Append(candidate.Strategy).Append('|').Append(candidate.Outcome.Reason ?? "merges");
            foreach (var spelling in candidate.Spellings) key.Append("|s:").Append(spelling);
            foreach (var c in candidate.Contributors)
                key.Append("|c:").Append(c.PackageId).Append('/').Append(c.RelPath).Append('=').Append(c.Hash).Append('@').Append(c.Priority).Append(' ').Append(c.ModName);
            if (inputs.GameBaseOf.TryGetValue(candidate.Path, out var gameBase)) key.Append("|g:").Append(gameBase.Fingerprint);
            if (inputs.Stamps.TryGetValue(candidate.Path, out var stamp)) key.Append("|b:").Append(stamp);
            if (inputs.VanillaOf.TryGetValue(candidate.Path, out var vanilla)) key.Append("|v:").Append(vanilla.Fingerprint);
            key.Append('\n');
        }
        return key.ToString();
    }

    private readonly record struct Fetched(byte[]? Bytes, string Fingerprint, MergeException? Failure);

    private sealed class Inputs
    {
        public Dictionary<string, Fetched> VanillaOf { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Fetched> GameBaseOf { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Stamps { get; } = new(StringComparer.Ordinal);

        /// <summary>The installed build; set once the game's own regulation has been read, since it is identified against that file.</summary>
        public Func<BuildIdentity> Live { get; set; } = () => throw new MergeException("the installed build was not read");

        public Dictionary<string, List<IMergeNote>> Notes { get; } = new(StringComparer.Ordinal);

        public byte[]? Vanilla(string path) => Take(VanillaOf[path]);

        public byte[] GameBase(string path) => Take(GameBaseOf[path]) ?? throw new InvalidOperationException($"No game file was read for {path}");

        private static byte[]? Take(Fetched fetched)
        {
            if (fetched.Failure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
            return fetched.Bytes;
        }

        /// <summary>Adds what the sources said about each path's ancestor (which dump served it, what an apply would keep) to that path's notes.</summary>
        public MergePlan WithSourceNotes(MergePlan plan)
            => plan with
            {
                Merged = plan.Merged
                    .Select(m => Notes.TryGetValue(m.Path, out var extra) && extra.Count > 0 ? m with { Notes = [.. m.Notes, .. extra] } : m)
                    .ToArray(),
            };
    }
}
