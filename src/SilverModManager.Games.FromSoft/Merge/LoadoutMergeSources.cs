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
public class LoadoutMergeSources(IFileStore fileStore, RegulationBaselines baselines, VanillaDumps dumps, InstalledBuilds builds, ILogger<LoadoutMergeSources> logger)
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
                    logger.LogWarning("Package {Package} is in no mod with a priority, so it ranks below every other mod when its files are merged", parts[0]);
            }

            var name = (mod ?? (chain.Length > 1 ? chain[1] : chain[0])).Name;
            providers.Add(new ProviderFile(parts[0], parts[1], file.Hash, (priority ?? ConflictPriority.MinValue).Value, name));
        }

        return providers;
    }

    /// <summary>The paths that more than one mod provides, and what the app would do about each.</summary>
    public virtual MergePathIndex Index(Loadout.ReadOnly loadout)
    {
        var providers = Providers(loadout);
        var vanillaPaths = dumps.Paths(loadout.Db, loadout.InstallationInstance, providers.Select(p => p.RelPath).Distinct(StringComparer.OrdinalIgnoreCase));
        return MergePathIndex.Build(providers, TraitsOf(loadout.InstallationInstance), vanillaPaths);
    }

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
    /// The vanilla file the contributors branched from, or null when nothing here can supply one for
    /// <paramref name="relPath"/>.
    /// <para>
    /// For the regulation the result always has the contributors' version: the merge folds from one ancestor, so the
    /// contributors have to agree on it, and it is looked for in the kept baselines, then in the vanilla dumps of the
    /// library, then in the installed game, but only if the game is at that version. Anything else is refused, never
    /// substituted.
    /// </para>
    /// <para>
    /// A packed file's vanilla copy is the member of a vanilla dump. With several dumps on offer the one of the
    /// contributors' regulation version wins when they ship one, otherwise the newest not newer than the installed
    /// build, otherwise the newest. Each copy served is named in <paramref name="notes"/> with its archive.
    /// </para>
    /// </summary>
    /// <param name="notes">Receives what a read-only call would have kept had it been an apply, and which dump served the file.</param>
    /// <exception cref="MergeException">The contributors disagree on a version, or no vanilla regulation of theirs is available.</exception>
    public byte[]? Vanilla(Loadout.ReadOnly loadout, string relPath, IReadOnlyList<ProviderFile> contributors, MergeSourceMode mode, ICollection<IMergeNote>? notes = null)
    {
        var installation = loadout.InstallationInstance;
        var traits = TraitsOf(installation);
        if (traits.RegulationFileName is not { } regulationName || !relPath.Equals(regulationName, StringComparison.OrdinalIgnoreCase))
            return PackedVanilla(loadout, relPath, contributors, notes);
        if (contributors.Count == 0) throw new ArgumentException("A vanilla regulation is looked up for the files that provide it.", nameof(contributors));

        var versions = contributors.Select(c => (File: c, Version: VersionOf(installation, c))).ToArray();
        if (versions.Select(v => v.Version).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            var named = string.Join(", ", versions.Select(v => $"{v.File.PackageId} is version {v.Version}"));
            throw new MergeException($"the mods ship regulations of different versions and a merge has one vanilla file to compare against: {named}");
        }

        var version = versions[0].Version;
        var vanilla = FindBaseline(version) ?? FromDump(loadout, regulationName, version, notes) ?? FromInstalledGame(installation, regulationName, version, mode, notes);

        // A hand-placed or damaged baseline must not slip an off-version file into the ancestor slot.
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
        return vanilla;
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
        var file = FromSoftPaths.GameFolder(game.Layout, installation) / relPath;
        try
        {
            return File.ReadAllBytes(file.GetFullPath());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new MergeException($"can't read {relPath} from the game folder ({exception.Message}); refusing rather than merging onto a mod's older snapshot");
        }
    }

    private byte[]? FromDump(Loadout.ReadOnly loadout, string regulationName, string version, ICollection<IMergeNote>? notes)
    {
        var dump = dumps.Find(loadout.Db, loadout.InstallationInstance).FirstOrDefault(d => d.Version.Equals(version, StringComparison.Ordinal));
        if (dump is null) return null;

        notes?.Add(new VanillaServedFromDump(regulationName, dump.ArchiveName, dump.Version));
        return dumps.Member(dump, regulationName);
    }

    private byte[]? PackedVanilla(Loadout.ReadOnly loadout, string relPath, IReadOnlyList<ProviderFile> contributors, ICollection<IMergeNote>? notes)
    {
        var installation = loadout.InstallationInstance;
        var offering = dumps.Find(loadout.Db, installation).Where(d => d.TryResolve(relPath, out _, out _)).ToArray();
        if (offering.Length == 0) return null;

        var dump = offering.Length == 1 ? offering[0] : Choose(loadout, contributors, offering);
        notes?.Add(new VanillaServedFromDump(relPath, dump.ArchiveName, dump.Version));
        return dumps.Member(dump, relPath);
    }

    private VanillaDump Choose(Loadout.ReadOnly loadout, IReadOnlyList<ProviderFile> contributors, IReadOnlyList<VanillaDump> offering)
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
        if (shipped.Length == 1 && offering.FirstOrDefault(d => d.Version.Equals(shipped[0], StringComparison.Ordinal)) is { } exact) return exact;

        string? installed = null;
        try
        {
            installed = builds.Identify(installation).RegulationVersion;
        }
        catch (InvalidOperationException exception)
        {
            logger.LogDebug(exception, "The installed build could not be identified, so the newest vanilla dump is used");
        }

        var byVersion = offering.OrderByDescending(d => d.Version, Comparer<string>.Create(CompareVersions)).ToArray();
        return byVersion.FirstOrDefault(d => installed is null || CompareVersions(d.Version, installed) <= 0) ?? byVersion[0];
    }

    // Build stamps are digit strings of equal length in practice; comparing by number keeps a shorter one from outranking a longer.
    private static int CompareVersions(string a, string b)
        => long.TryParse(a, out var x) && long.TryParse(b, out var y) ? x.CompareTo(y) : string.CompareOrdinal(a, b);

    private byte[]? FindBaseline(string version)
    {
        try
        {
            return baselines.Find(version) is { } kept ? File.ReadAllBytes(kept.GetFullPath()) : null;
        }
        catch (ArgumentException exception)
        {
            throw new MergeException($"version {version} cannot be looked up: {exception.Message}");
        }
    }

    private byte[] FromInstalledGame(GameInstallation installation, string regulationName, string version, MergeSourceMode mode, ICollection<IMergeNote>? notes)
    {
        var game = GameOf(installation);
        BuildIdentity live;
        try
        {
            live = builds.Identify(installation);
        }
        catch (InvalidOperationException exception)
        {
            throw new MergeException($"no vanilla regulation of version {version} is available, and the installed game could not be identified: {exception.Message}");
        }

        if (live.RegulationVersion != version || live.RegulationHash is not { } identified)
            throw new MergeException($"no vanilla regulation of version {version} is available; the mods were built for it and the game is at {live.RegulationVersion ?? "no known regulation version"}");

        try
        {
            if (mode == MergeSourceMode.Apply)
                return File.ReadAllBytes(baselines.Adopt(installation, live).GetFullPath());

            var bytes = File.ReadAllBytes((FromSoftPaths.GameFolder(game.Layout, installation) / regulationName).GetFullPath());
            if (!Regulation.Sha256Hex(bytes).Equals(identified, StringComparison.OrdinalIgnoreCase))
                throw new MergeException("the game's regulation changed while it was being read; run again");
            notes?.Add(new BaselineWillBeKept(version));
            return bytes;
        }
        catch (Exception exception) when (exception is HealException or IOException or UnauthorizedAccessException)
        {
            throw new MergeException($"the installed regulation of version {version} cannot be used as the vanilla one: {exception.Message}");
        }
    }

    private static IFromSoftGame GameOf(GameInstallation installation)
        => installation.Game as IFromSoftGame
           ?? throw new InvalidOperationException($"{installation.Game.DisplayName} is not a FromSoft game, so it has nothing to merge.");

    private static FromSoftFormatTraits TraitsOf(GameInstallation installation) => GameOf(installation).Formats;
}
