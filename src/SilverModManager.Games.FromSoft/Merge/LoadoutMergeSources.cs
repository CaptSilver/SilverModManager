using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Heal;
using SilverModManager.Games.FromSoft.Me3;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>Whether a read of the merge inputs may keep anything on disk.</summary>
public enum MergeSourceMode
{
    /// <summary>The deployment is being built, so a vanilla regulation worth keeping is kept.</summary>
    Apply,

    /// <summary>Planning, Health Check or a report: nothing is written.</summary>
    Report,
}

/// <summary>
/// Everything the merge index and the merge planner need from the app: which enabled files provide each path and at
/// what priority, their bytes, the vanilla regulation the contributors were built against, and the game's own file
/// to start a param merge from. The index built for the synchronizer and the one built for a preflight come from the
/// same query, so they cannot disagree.
/// <para>
/// Re-entrancy: the index is built while the synchronizer's <c>intrinsic_files</c> table function runs, inside the
/// winning-files query, so everything it needs is read through the model accessors (<c>FindByLoadout</c>,
/// <c>FindByTarget</c>) and never through a database query, which would re-enter the one that is running.
/// </para>
/// <para>
/// The vanilla and the game's file are separate questions with separate code. <see cref="Vanilla"/> is the ancestor
/// of a three-way merge and must be a regulation of the version the contributors were built for: the game's file of
/// any other version in that slot would read every row the patch added as a mod deletion. <see cref="GameBase"/> is
/// the file a param merge starts from, and is always the installed one. A packed file has no game-base branch: the
/// regulation is the only strategy that starts from the game's file and it is loose. Whether a mod's copy of a packed
/// asset should lose to the game's is a choice the mod's author makes, with no rule to derive it from, so it is not
/// decided here.
/// </para>
/// </summary>
public class LoadoutMergeSources(IFileStore fileStore, RegulationBaselines baselines, VanillaDumps dumps, ArchiveVanillas archives, InstalledBuilds builds, ILogger<LoadoutMergeSources> logger)
{
    private readonly ConcurrentDictionary<Hash, string> _versions = new();
    private readonly ConcurrentDictionary<string, byte> _unprioritised = new(StringComparer.Ordinal);

    /// <summary>
    /// Every enabled file under a package folder of the mods location, with the priority of the mod that provides it.
    /// Natives and the overlay are not packages and are left out.
    /// </summary>
    public IReadOnlyList<ProviderFile> Providers(Loadout.ReadOnly loadout)
    {
        var db = loadout.Db;
        var rows = new Dictionary<LoadoutItemGroupId, ConflictPriority?>();
        var providers = new List<ProviderFile>();

        foreach (var item in LoadoutItem.FindByLoadout(db, loadout).OfTypeLoadoutItemWithTargetPath())
        {
            var chain = item.AsLoadoutItem().GetThisAndParents().ToArray();
            if (chain.Any(p => p.IsDisabled)) continue;

            var target = (GamePath)item.TargetPath;
            if (target.LocationId != LocationId.Me3Mods) continue;

            var parts = target.Path.Path.Split('/', 2);
            if (parts.Length < 2) continue;
            if (parts[0].Equals(Me3ModsLayout.OverlayFolder, StringComparison.OrdinalIgnoreCase)) continue;
            if (parts[0].Equals(Me3ModsLayout.NativesFolder, StringComparison.OrdinalIgnoreCase)) continue;

            // Only a file has bytes to merge; anything else with a target path is a marker.
            if (!item.TryGetAsLoadoutFile(out var file)) continue;

            // The installer puts the priority on the mod's group, and a collection's rows are remapped onto the mods
            // inside it, so the first group above the file that has a row is the mod, collection or not.
            LoadoutItem.ReadOnly? mod = null;
            ConflictPriority? priority = null;
            foreach (var ancestor in chain.Skip(1))
            {
                var id = LoadoutItemGroupId.From(ancestor.LoadoutItemId.Value);
                if (!rows.TryGetValue(id, out var row))
                    rows[id] = row = LoadoutItemGroupPriority.FindByTarget(db, id).Select(r => (ConflictPriority?)r.Priority).Max();
                if (row is null) continue;

                mod = ancestor;
                priority = row;
                break;
            }

            if (priority is null)
            {
                if (_unprioritised.TryAdd(parts[0], 0))
                    logger.LogWarning("Package {Package} is in no mod with a priority, so it ranks below every other mod when its files are merged, and above them when me3 picks between whole copies", parts[0]);
            }

            var owner = mod ?? (chain.Length > 1 ? chain[1] : chain[0]);
            providers.Add(new ProviderFile(parts[0], parts[1], file.Hash, (priority ?? ConflictPriority.MinValue).Value, owner.Name, owner.LoadoutItemId, priority is not null));
        }

        return providers;
    }

    /// <summary>The paths that more than one mod provides, and what the app would do about each.</summary>
    public virtual MergePathIndex Index(Loadout.ReadOnly loadout)
    {
        var providers = Providers(loadout);
        var installation = loadout.InstallationInstance;
        var vanillaPaths = dumps.Paths(loadout.Db, installation, providers.Select(p => p.RelPath).Distinct(StringComparer.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        vanillaPaths.UnionWith(GameArchivePaths(installation, providers, vanillaPaths));
        return MergePathIndex.Build(providers, TraitsOf(installation), vanillaPaths);
    }

    /// <summary>
    /// The overlapping packed paths that no dump offers but the game's own archives hold, in the spelling they were
    /// provided in. Only a path that is contested and would use a vanilla copy is looked up: the first lookup decrypts
    /// the archives' indexes, which is not worth doing for a loadout with nothing to merge.
    /// </summary>
    private IReadOnlySet<string> GameArchivePaths(GameInstallation installation, IReadOnlyList<ProviderFile> providers, IReadOnlySet<string> offered)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var traits = TraitsOf(installation);
        if (traits.Archives.IsEmpty) return found;

        var folder = ArchiveFolder(installation);
        var warned = false;
        foreach (var group in providers.GroupBy(p => p.RelPath, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Select(p => p.PackageId).Distinct(StringComparer.Ordinal).Count() < 2) continue;
            if (offered.Contains(group.Key)) continue;
            if (MergeStrategies.ForPath(group.Key, traits, vanillaAvailable: true) is not { } strategy
                || !MergeStrategies.NeedsVanilla(strategy) || MergeStrategies.TakesGameBase(strategy)) continue;

            try
            {
                if (GameArchives.Find(folder, group.Key, traits) is not null) found.UnionWith(group.Select(p => p.RelPath));
            }
            catch (Bhd5Exception exception)
            {
                // Archives are searched in order, so a path an earlier archive holds is still found past a broken later
                // one. The path falls back to what it does without a vanilla copy, and the report says why.
                if (!warned) logger.LogWarning(exception, "The game's archives could not be searched for a vanilla copy of {Path}", group.Key);
                warned = true;
            }
        }
        return found;
    }

    /// <summary>
    /// Why the game's own archives could not be searched for vanilla copies, as a sentence for the report; null when
    /// every archive the install ships opens, or the game packs nothing.
    /// </summary>
    public string? GameArchiveFailure(GameInstallation installation)
    {
        var traits = TraitsOf(installation);
        if (traits.Archives.IsEmpty) return null;
        return GameArchives.Unreadable(ArchiveFolder(installation), traits) is var (archive, problem)
            ? $"the game's archive {archive.Name}.bhd could not be read, so it was not searched for a vanilla copy: {problem}"
            : null;
    }

    private string ArchiveFolder(GameInstallation installation) => FromSoftPaths.GameFolder(GameOf(installation).Layout, installation).GetFullPath();

    /// <summary>
    /// A provider's bytes from the file store. Read synchronously because the planner is a pure function over byte
    /// sources; it never goes through the deployment backend or the mods folder.
    /// </summary>
    public virtual byte[] ContributorBytes(ProviderFile file)
    {
        using var stream = fileStore.GetFileStream(file.Hash).GetAwaiter().GetResult();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// The build stamp of a provider's regulation. Reading it decrypts the file, so it is only asked on the paths
    /// that already read bytes (the planner, Health Check, reports) and is remembered by hash.
    /// </summary>
    public string VersionOf(GameInstallation installation, ProviderFile regulation)
    {
        if (_versions.TryGetValue(regulation.Hash, out var known)) return known;

        var traits = TraitsOf(installation);
        string version;
        try
        {
            version = Regulation.Version(Regulation.Unpack(ContributorBytes(regulation), traits));
        }
        catch (Exception exception) when (exception is RegulationException or AesException or DcxException or KrakenException)
        {
            throw new MergeException($"{regulation.PackageId}/{regulation.RelPath} is not a regulation this game's key opens: {exception.Message}");
        }

        _versions[regulation.Hash] = version;
        return version;
    }

    /// <summary>
    /// The vanilla file each contributor branched from, or null when nothing here can supply one for
    /// <paramref name="relPath"/>.
    /// <para>
    /// For the regulation the contributors are grouped by the build stamp they carry and each group gets a vanilla of
    /// its own build: the kept baselines, then the vanilla dumps of the library, then the installed game, but only if
    /// the game is at that build, then any regulation of that build inside a library archive that passes the checks of
    /// a vanilla (<see cref="VanillaValidation"/>). A file of another build is never substituted, since every row the patch added would
    /// read as a mod deletion. A build with no vanilla is refused together with every other such build, so one
    /// message lists what is missing.
    /// </para>
    /// <para>
    /// A packed file's vanilla copy is the member of a vanilla dump or the game's own copy, the same for every
    /// contributor. With several on offer the one the contributors' files differ from least wins, measured in the
    /// strategy's own units (<see cref="VanillaCloseness.Closest"/>). A tie goes to a dump before the game's archives,
    /// and among dumps to the one of the contributors' regulation version when they ship one, otherwise the newest not
    /// newer than the installed build, otherwise the newest. Each copy served is named in <paramref name="notes"/> with
    /// its archive and, when the dump's regulation was only validated, says so.
    /// </para>
    /// </summary>
    /// <param name="notes">Receives what a read-only call would have kept had it been an apply, and which source served each build's vanilla.</param>
    /// <exception cref="MergeException">A contributor's build has no vanilla regulation available; <see cref="MergeException.Missing"/> lists each.</exception>
    public VanillaAncestors? Ancestors(Loadout.ReadOnly loadout, string relPath, IReadOnlyList<ProviderFile> contributors, MergeSourceMode mode, ICollection<IMergeNote>? notes = null)
    {
        var installation = loadout.InstallationInstance;
        var traits = TraitsOf(installation);
        if (traits.RegulationFileName is not { } regulationName || !relPath.Equals(regulationName, StringComparison.OrdinalIgnoreCase))
        {
            return PackedVanilla(loadout, relPath, contributors, notes) is { } packed
                ? new VanillaAncestors([new AncestorGroup(null, contributors, packed)])
                : null;
        }
        if (contributors.Count == 0) throw new ArgumentException("A vanilla regulation is looked up for the files that provide it.", nameof(contributors));

        var byVersion = contributors.GroupBy(c => VersionOf(installation, c), StringComparer.Ordinal).ToArray();
        var groups = new List<AncestorGroup>();
        var sources = new List<VanillaGroupSource>();
        var groupNotes = new List<IMergeNote>();
        var missing = new List<VanillaMissing>();
        foreach (var members in byVersion)
        {
            var version = members.Key;
            var mods = members.Select(m => m.ModName).Distinct(StringComparer.Ordinal).ToArray();
            var tried = new List<string>();
            var served = FromBaselines(version, members.ToArray(), traits, groupNotes, tried)
                         ?? FromDump(loadout, regulationName, version, groupNotes, tried)
                         ?? FromInstalledGame(installation, regulationName, version, mode, groupNotes, tried)
                         ?? FromArchiveMember(loadout, regulationName, members.ToArray(), mode, groupNotes, tried);
            if (served is null)
            {
                missing.Add(new VanillaMissing(version, mods, tried, members.Select(m => m.ModId).OfType<LoadoutItemId>().Distinct().ToArray()));
                continue;
            }

            CheckStamp(served.Bytes, version, traits);
            groups.Add(new AncestorGroup(version, members.ToArray(), served.Bytes));
            sources.Add(new VanillaGroupSource(version, mods, served.Source));
        }

        if (missing.Count > 0)
            throw new MergeException(string.Join("\n", missing.Select(m =>
                $"no vanilla regulation of version {m.Version} is available; {string.Join(", ", m.Mods)} {(m.Mods.Count == 1 ? "was" : "were")} built for it ({string.Join("; ", m.Tried)})")), missing);

        if (notes is not null)
        {
            foreach (var note in groupNotes) notes.Add(note);
            // With one build there is one source and the notes above already say it; several need a label per build.
            if (sources.Count > 1) foreach (var source in sources) notes.Add(source);
        }
        return new VanillaAncestors(groups, sources);
    }

    /// <summary>
    /// Each build the loadout's regulation mods were made for, with the vanilla it would be compared against. Unlike
    /// <see cref="Ancestors"/> a build without a vanilla does not hide the others: it is listed with what was tried.
    /// Reads only, as a report does, so nothing is kept.
    /// </summary>
    /// <exception cref="MergeException">A mod's regulation cannot be read.</exception>
    public IReadOnlyList<VanillaGroupListing> VanillaGroups(Loadout.ReadOnly loadout)
    {
        var installation = loadout.InstallationInstance;
        if (TraitsOf(installation).RegulationFileName is not { } regulationName) return [];

        var contributors = Providers(loadout).Where(p => p.RelPath.Equals(regulationName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var listing = new List<VanillaGroupListing>();
        foreach (var members in contributors.GroupBy(c => VersionOf(installation, c), StringComparer.Ordinal))
        {
            var mods = members.Select(m => m.ModName).Distinct(StringComparer.Ordinal).ToArray();
            try
            {
                var ancestors = Ancestors(loadout, regulationName, members.ToArray(), MergeSourceMode.Report)
                                ?? throw new InvalidOperationException($"No vanilla lookup was made for {regulationName}, which is this game's regulation.");
                listing.Add(new VanillaGroupListing(members.Key, mods, ancestors.Sources.Single().Source, []));
            }
            catch (MergeException exception) when (exception.Missing.Count > 0)
            {
                listing.Add(new VanillaGroupListing(members.Key, mods, null, exception.Missing.SelectMany(m => m.Tried).ToArray()));
            }
        }
        return listing;
    }

    /// <summary>
    /// The game's own file at <paramref name="relPath"/>, which a param merge starts from so the result carries the
    /// installed build's rows. Refuses when it cannot be read rather than merge onto an older snapshot a mod shipped.
    /// </summary>
    /// <exception cref="MergeException">The file is not in the game folder or cannot be read.</exception>
    public byte[] GameBase(GameInstallation installation, string relPath)
    {
        if (relPath.Split('/', '\\').Contains(".."))
            throw new MergeException($"{relPath} points outside the game folder, so it cannot be the game's file");

        var game = GameOf(installation);
        try
        {
            return File.ReadAllBytes(FindIgnoringCase(FromSoftPaths.GameFolder(game.Layout, installation).GetFullPath(), relPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new MergeException($"can't read {relPath} from the game folder ({exception.Message}); refusing rather than merging onto a mod's older snapshot");
        }
    }

    /// <summary>
    /// The game's file for <paramref name="relPath"/> whatever case a mod spelled it in: the game folder is
    /// case-sensitive on Linux, but the game ships <c>regulation.bin</c> and a mod may call it <c>Regulation.bin</c>.
    /// A segment with no match is kept as spelled, so the read fails naming the path the mod gave.
    /// </summary>
    private static string FindIgnoringCase(string folder, string relPath)
    {
        var current = folder;
        foreach (var segment in relPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            var exact = System.IO.Path.Combine(current, segment);
            if (File.Exists(exact) || Directory.Exists(exact) || !Directory.Exists(current))
            {
                current = exact;
                continue;
            }

            var match = Directory.EnumerateFileSystemEntries(current)
                .Where(e => System.IO.Path.GetFileName(e).Equals(segment, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal).FirstOrDefault();
            current = match ?? exact;
        }
        return current;
    }

    private Served? FromDump(Loadout.ReadOnly loadout, string regulationName, string version, ICollection<IMergeNote> notes, ICollection<string> tried)
    {
        var dump = dumps.Find(loadout.Db, loadout.InstallationInstance).FirstOrDefault(d => d.Version.Equals(version, StringComparison.Ordinal));
        if (dump is null)
        {
            tried.Add($"vanilla dumps in the library (none of version {version})");
            return null;
        }

        notes.Add(new VanillaServedFromDump(regulationName, dump.ArchiveName, dump.Version));
        return new Served(dumps.Member(dump, regulationName), $"the vanilla copy in the library archive {dump.ArchiveName}");
    }

    private byte[]? PackedVanilla(Loadout.ReadOnly loadout, string relPath, IReadOnlyList<ProviderFile> contributors, ICollection<IMergeNote>? notes)
    {
        var installation = loadout.InstallationInstance;
        var offering = dumps.Find(loadout.Db, installation).Where(d => d.TryResolve(relPath, out _, out _)).ToArray();
        var ranked = offering.Length > 1 ? Rank(loadout, contributors, offering) : offering;

        // The game's own copy is the installed build's, which a mod may have been made before: every change the patch
        // made then reads as the mod's edit. So it is a candidate beside the dumps, and when there is a choice the copy
        // nearest the contributors' files is used. A tie goes to a dump, then to the dump the build rules prefer.
        var game = FromGameArchives(installation, relPath);
        if (ranked.Length == 0) return game is null ? null : ServedFromGame(game, relPath, notes);
        if (game is null && ranked.Length == 1) return ServedFromDumpOf(ranked[0], relPath, notes);

        var dumpBytes = ranked.Select(d => dumps.Member(d, relPath)).ToList();
        var candidates = game is null ? dumpBytes : [.. dumpBytes, game];
        var (index, distance) = VanillaCloseness.Closest(candidates, contributors.Select(ContributorBytes).ToArray(), relPath, TraitsOf(installation));
        if (game is not null && index == dumpBytes.Count) return ServedFromGame(game, relPath, notes, distance, candidates.Count);

        var chosen = ranked[index];
        if (distance is { } measured) notes?.Add(new VanillaNearestChosen($"archive {chosen.ArchiveName}", measured, candidates.Count));
        return ServedFromDumpOf(chosen, relPath, notes);
    }

    private byte[] ServedFromDumpOf(VanillaDump dump, string relPath, ICollection<IMergeNote>? notes)
    {
        notes?.Add(new VanillaServedFromDump(relPath, dump.ArchiveName, dump.Version, dump.Validated));
        return dumps.Member(dump, relPath);
    }

    private static byte[] ServedFromGame(byte[] bytes, string relPath, ICollection<IMergeNote>? notes, long? distance = null, int of = 1)
    {
        notes?.Add(new VanillaServedFromGameArchive(relPath));
        if (distance is { } measured) notes?.Add(new VanillaNearestChosen("the game's own archives", measured, of));
        return bytes;
    }

    // The game's copy of a packed file, or null when the archives hold none or cannot be opened.
    private byte[]? FromGameArchives(GameInstallation installation, string relPath)
    {
        var traits = TraitsOf(installation);
        if (traits.Archives.IsEmpty) return null;

        var folder = ArchiveFolder(installation);
        try
        {
            return GameArchives.Find(folder, relPath, traits) is null ? null : GameArchives.ReadFile(folder, relPath, traits);
        }
        catch (Bhd5Exception exception)
        {
            // An archive that can't be opened is the same as one with no copy: the merge is refused as having no vanilla
            // source, and the log says why.
            logger.LogWarning(exception, "The game's archives could not supply a vanilla copy of {Path}", relPath);
            return null;
        }
    }

    // The dumps in the order the build rules prefer them, which is where a tie in distance is settled.
    private VanillaDump[] Rank(Loadout.ReadOnly loadout, IReadOnlyList<ProviderFile> contributors, IReadOnlyList<VanillaDump> offering)
    {
        var installation = loadout.InstallationInstance;
        var regulationName = TraitsOf(installation).RegulationFileName;
        // Only a regulation shipped beside the contributors' copies says which build they branched from.
        var packages = contributors.Select(c => c.PackageId).ToHashSet(StringComparer.Ordinal);
        var shipped = Providers(loadout)
            .Where(p => packages.Contains(p.PackageId) && p.RelPath.Equals(regulationName, StringComparison.OrdinalIgnoreCase))
            .Select(p => VersionOf(installation, p))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var exact = shipped.Length == 1
            ? offering.Where(d => d.Version.Equals(shipped[0], StringComparison.Ordinal)).ToArray()
            : [];

        string? installed = null;
        try
        {
            installed = builds.Identify(installation).RegulationVersion;
        }
        catch (InvalidOperationException exception)
        {
            logger.LogDebug(exception, "The installed build could not be identified, so the newest vanilla dump is used");
        }

        var byVersion = offering.Except(exact).OrderByDescending(d => d.Version, Comparer<string>.Create(CompareVersions)).ToArray();
        var notNewer = byVersion.Where(d => installed is null || CompareVersions(d.Version, installed) <= 0);
        return [.. exact, .. notNewer, .. byVersion.Except(notNewer)];
    }

    // Build stamps are digit strings of equal length in practice; comparing by number keeps a shorter one from outranking a longer.
    private static int CompareVersions(string a, string b)
        => long.TryParse(a, out var x) && long.TryParse(b, out var y) ? x.CompareTo(y) : string.CompareOrdinal(a, b);

    private Served? FromBaselines(string version, IReadOnlyList<ProviderFile> contributors, FromSoftFormatTraits traits, ICollection<IMergeNote> notes, ICollection<string> tried)
    {
        IReadOnlyList<AbsolutePath> kept;
        try
        {
            kept = baselines.FindAll(version);
        }
        catch (ArgumentException exception)
        {
            throw new MergeException($"version {version} cannot be looked up: {exception.Message}");
        }
        if (kept.Count == 0)
        {
            tried.Add($"kept baselines (none of version {version})");
            return null;
        }

        var files = kept.Select(k => (Name: Path.GetFileName(k.GetFullPath()), Bytes: File.ReadAllBytes(k.GetFullPath()))).ToArray();
        var chosen = files[0];
        if (files.Length > 1)
        {
            // A hotfix that did not bump the stamp leaves two vouched files under one version; the mods were made from one.
            var mods = contributors.Select(ContributorBytes).ToArray();
            try
            {
                chosen = files.MinBy(f => VanillaCloseness.Distance(f.Bytes, mods, traits));
            }
            catch (Exception exception) when (exception is RegulationException or AesException or DcxException or KrakenException or Bnd4Exception)
            {
                // The stamp read but the binder behind it did not; the merge would refuse this mod anyway, so refuse here rather than crash the plan.
                throw new MergeException($"a mod's regulation of version {version} cannot be read to choose between the {files.Length} vanilla regulations kept for it: {exception.Message}");
            }
            notes.Add(new VanillaAlternateChosen(version, chosen.Name, files.Length));
        }

        return new Served(chosen.Bytes, chosen.Name == files[0].Name ? ProvenanceText(version) : $"the vanilla regulation {chosen.Name}, one of {files.Length} kept for this build");
    }

    private string ProvenanceText(string version)
    {
        try
        {
            return baselines.Provenance(version)?.Describe() ?? "a kept vanilla regulation";
        }
        catch (HealException exception)
        {
            throw new MergeException($"the vanilla regulation kept for version {version} has unreadable provenance: {exception.Message}");
        }
    }

    // A hand-placed or damaged baseline must not slip an off-version file into the ancestor slot.
    private static void CheckStamp(byte[] vanilla, string version, FromSoftFormatTraits traits)
    {
        string got;
        try
        {
            got = Regulation.Version(Regulation.Unpack(vanilla, traits));
        }
        catch (Exception exception) when (exception is RegulationException or AesException or DcxException or KrakenException)
        {
            throw new MergeException($"the vanilla regulation kept for version {version} is not a regulation this game's key opens: {exception.Message}");
        }
        if (!got.Equals(version, StringComparison.Ordinal))
            throw new MergeException($"the vanilla regulation kept for version {version} is version {got}");
    }

    private sealed record Served(byte[] Bytes, string Source);

    private Served? FromInstalledGame(GameInstallation installation, string regulationName, string version, MergeSourceMode mode, ICollection<IMergeNote> notes, ICollection<string> tried)
    {
        var game = GameOf(installation);
        BuildIdentity live;
        try
        {
            live = builds.Identify(installation);
        }
        catch (InvalidOperationException exception)
        {
            tried.Add($"the installed game, which could not be identified: {exception.Message}");
            return null;
        }

        if (live.RegulationVersion != version || live.RegulationHash is not { } identified)
        {
            tried.Add($"the installed game is at {live.RegulationVersion ?? "no known regulation version"}");
            return null;
        }

        try
        {
            if (mode == MergeSourceMode.Apply)
                return new Served(File.ReadAllBytes(baselines.Adopt(installation, live).GetFullPath()), "the installed game's regulation, kept as this build's baseline");

            var bytes = File.ReadAllBytes((FromSoftPaths.GameFolder(game.Layout, installation) / regulationName).GetFullPath());
            if (!Regulation.Sha256Hex(bytes).Equals(identified, StringComparison.OrdinalIgnoreCase))
                throw new MergeException("the game's regulation changed while it was being read; run again");
            notes.Add(new BaselineWillBeKept(version));
            return new Served(bytes, "the installed game's regulation, which the next apply keeps as this build's baseline");
        }
        catch (Exception exception) when (exception is HealException or IOException or UnauthorizedAccessException)
        {
            throw new MergeException($"the installed regulation of version {version} cannot be used as the vanilla one: {exception.Message}");
        }
    }

    /// <summary>
    /// The last resort for a build: a regulation of the contributors' stamp that some library archive holds, which a
    /// randomizer or a mod pack often bundles as the file it worked from. Nothing outside the file vouches for it, so it
    /// has to pass the checks of a vanilla against these very mods, and it is kept in the validated tier.
    /// </summary>
    private Served? FromArchiveMember(Loadout.ReadOnly loadout, string regulationName, IReadOnlyList<ProviderFile> contributors, MergeSourceMode mode, ICollection<IMergeNote> notes, ICollection<string> tried)
    {
        var installation = loadout.InstallationInstance;
        var traits = TraitsOf(installation);
        var version = VersionOf(installation, contributors[0]);
        var candidates = archives.Find(loadout.Db, traits, version, contributors.Select(c => c.Hash).ToHashSet());
        if (candidates.Count == 0)
        {
            tried.Add($"regulations inside library archives (none of version {version} besides the mods' own)");
            return null;
        }

        var mods = contributors.Select(c => (c.ModName, ContributorBytes(c))).ToArray();
        var game = GameBaseOrNull(installation, regulationName);
        foreach (var candidate in candidates)
        {
            var bytes = archives.Read(candidate.Hash);
            var verdict = VanillaValidation.Check(bytes, game, mods, traits);
            if (!verdict.Passed)
            {
                tried.Add($"{candidate.ArchiveName} ({candidate.Member}) is version {version} but was refused as a vanilla: {verdict.Summary}");
                continue;
            }

            var provenance = baselines.ValidatedProvenance(bytes, BaselineSource.LibraryArchive, $"{candidate.ArchiveName}: {candidate.Member}", $"archive {candidate.ArchiveName}, member {candidate.Member}; {verdict.Summary}");
            if (mode == MergeSourceMode.Apply) baselines.KeepValidated(version, bytes, provenance);
            else notes.Add(new BaselineWillBeKept(version));
            return new Served(bytes, provenance.Describe());
        }
        return null;
    }

    /// <summary>
    /// Keeps a regulation the user points at as the validated vanilla of its build, when it passes the same checks as an
    /// archive member against the loadout's mods of that build. Without such mods there is nothing to check it against
    /// and it is refused. A file a Steam manifest lists needs no checks and is kept vouched instead.
    /// </summary>
    /// <exception cref="HealException">The file can't be read, isn't a regulation of the expected build, no mod of the loadout was built for it, or it failed a check.</exception>
    public RegulationImport KeepUserVanilla(Loadout.ReadOnly loadout, AbsolutePath file, string? expectedVersion)
    {
        var installation = loadout.InstallationInstance;
        var traits = TraitsOf(installation);
        byte[] bytes;
        string version;
        try
        {
            bytes = File.ReadAllBytes(file.GetFullPath());
            version = Regulation.Version(Regulation.Unpack(bytes, traits));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or RegulationException or AesException or DcxException or KrakenException)
        {
            throw new HealException($"{file} is not a regulation this game's key opens: {exception.Message}");
        }
        if (expectedVersion is not null && !version.Equals(expectedVersion, StringComparison.Ordinal))
            throw new HealException($"{file} is regulation build {version}, not {expectedVersion}.");
        if (baselines.ListedBySteam(installation, bytes)) return baselines.Import(installation, file, expectedVersion);

        var regulationName = traits.RegulationFileName ?? throw new HealException($"{GameOf(installation).DisplayName} has no regulation file.");
        List<ProviderFile> contributors;
        try
        {
            contributors = Providers(loadout).Where(p => p.RelPath.Equals(regulationName, StringComparison.OrdinalIgnoreCase) && VersionOf(installation, p) == version).ToList();
        }
        catch (MergeException exception)
        {
            throw new HealException(exception.Message);
        }
        if (contributors.Count == 0)
            throw new HealException($"No mod in this loadout ships a regulation of build {version}, so there is nothing to check {file} against; it can only be kept as a vanilla once it is checked against the mods built from it.");

        var verdict = VanillaValidation.Check(bytes, GameBaseOrNull(installation, regulationName), contributors.Select(c => (c.ModName, ContributorBytes(c))).ToArray(), traits);
        if (!verdict.Passed) throw new HealException($"{file} was refused as a vanilla regulation of build {version}: {verdict.Summary}");

        var provenance = baselines.ValidatedProvenance(bytes, BaselineSource.UserFile, file.GetFullPath(), verdict.Summary);
        var before = baselines.Provenance(version);
        if (before is not null && before.Sha256 != provenance.Sha256)
            throw new HealException($"Build {version} already has a baseline kept, and a kept baseline is not replaced by a file that is only validated.");

        var kept = baselines.KeepValidated(version, bytes, provenance);
        return new RegulationImport(kept, version, before is null ? ImportOutcome.Kept : ImportOutcome.AlreadyKept, before ?? provenance);
    }

    // The layout check is skipped, and says so, when the game's file can't be read: the merge itself refuses that case.
    private byte[]? GameBaseOrNull(GameInstallation installation, string regulationName)
    {
        try
        {
            return GameBase(installation, regulationName);
        }
        catch (MergeException)
        {
            return null;
        }
    }

    private static IFromSoftGame GameOf(GameInstallation installation)
        => installation.Game as IFromSoftGame
           ?? throw new InvalidOperationException($"{installation.Game.DisplayName} is not a FromSoft game, so it has nothing to merge.");

    private static FromSoftFormatTraits TraitsOf(GameInstallation installation) => GameOf(installation).Formats;
}

/// <summary>One build of a loadout's regulation mods and the vanilla they would be compared against.</summary>
/// <param name="Version">The build stamp the mods carry.</param>
/// <param name="Mods">The mods made for it.</param>
/// <param name="Source">Where the vanilla comes from, as a phrase; null when none is available.</param>
/// <param name="Tried">Each place a vanilla was looked for when there is none.</param>
public sealed record VanillaGroupListing(string Version, IReadOnlyList<string> Mods, string? Source, IReadOnlyList<string> Tried);
