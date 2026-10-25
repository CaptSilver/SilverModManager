using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Formats;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Games.FromSoft.Heal;

/// <summary>
/// The vanilla regulations kept under the data root, one per build, as <c>regulation-&lt;version&gt;.bin</c>, each with
/// a <c>.provenance</c> file saying where it came from and how far it is trusted (<see cref="RegulationProvenance"/>).
/// A kept baseline is the file a param merge compares its contributors against. The app keeps every build it sees, so
/// the old build is still there after a game update, and a copy is never overwritten by another of the same trust:
/// a vouched file takes the place of a validated one, and a second vouched file under one build stamp (a hotfix that
/// kept the stamp) is kept beside the first as <c>regulation-&lt;version&gt;@&lt;sha1&gt;.bin</c>.
/// </summary>
/// <param name="root">The folder the baselines live in. Resolved on every call, so a changed data root is followed.</param>
/// <param name="vouching">Tells whether Steam shipped a file. Null when no manifests are available, which leaves every copy from an install vouched by that fact alone.</param>
/// <param name="time">Stamps the provenance.</param>
/// <param name="logger">Receives what <see cref="KeepBuild"/> could not do.</param>
public sealed class RegulationBaselines(Func<AbsolutePath> root, IManifestVouching? vouching = null, TimeProvider? time = null, ILogger? logger = null)
{
    private const string Prefix = "regulation-";
    private const string Suffix = ".bin";
    private const string SidecarSuffix = ".xxh3";
    private const string ProvenanceSuffix = ".provenance";
    private const char AlternateMark = '@';

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>The kept baseline of <paramref name="version"/>, or null when none was kept. Looks at the folder and nothing else.</summary>
    public AbsolutePath? Find(string version)
    {
        var file = PathOf(version);
        return file.FileExists ? file : null;
    }

    /// <summary>Every file kept for <paramref name="version"/>: the baseline <see cref="Find"/> answers first, then any vouched alternates in file name order.</summary>
    public IReadOnlyList<AbsolutePath> FindAll(string version)
    {
        var primary = PathOf(version);
        var found = new List<AbsolutePath>();
        if (primary.FileExists) found.Add(primary);

        var folder = root().GetFullPath();
        if (Directory.Exists(folder))
        {
            var pattern = Prefix + version + AlternateMark + "*" + Suffix;
            found.AddRange(Directory.EnumerateFiles(folder, pattern).Order(StringComparer.Ordinal)
                .Select(f => FileSystem.Shared.FromUnsanitizedFullPath(f)));
        }
        return found;
    }

    /// <summary>What is recorded about the baseline of <paramref name="version"/>, or null when none is kept. A baseline kept before provenance existed was copied out of an install; it is read as that and its provenance is written.</summary>
    /// <exception cref="HealException">The provenance file is there and cannot be read.</exception>
    public RegulationProvenance? Provenance(string version)
    {
        var file = PathOf(version);
        return file.FileExists ? ProvenanceOf(file.GetFullPath()) : null;
    }

    /// <summary>
    /// Every kept version with the xxHash3 of its file, for the baselines that are vouched. A validated baseline is
    /// left out, so the app never vouches for a library archive on the strength of a file it only guessed at. Where a
    /// version has alternates this is the first one; <see cref="VouchedHashes"/> has them all.
    /// </summary>
    /// <exception cref="HealException">A provenance file is there and cannot be read.</exception>
    public IReadOnlyDictionary<string, Hash> Known()
    {
        var known = new Dictionary<string, Hash>(StringComparer.Ordinal);
        foreach (var (version, file, _) in KeptFiles().Where(k => !k.IsAlternate))
        {
            if (VouchedHash(file) is { } hash) known[version] = hash;
        }
        return known;
    }

    /// <summary>The xxHash3 of every vouched file kept, alternates included.</summary>
    /// <exception cref="HealException">A provenance file is there and cannot be read.</exception>
    public IReadOnlySet<Hash> VouchedHashes()
        => KeptFiles().Select(k => VouchedHash(k.File)).OfType<Hash>().ToHashSet();

    /// <summary>Every file kept, by build and then file name, with where it came from and how far it is trusted.</summary>
    /// <exception cref="HealException">A provenance file is there and cannot be read.</exception>
    public IReadOnlyList<KeptBaseline> List()
        => KeptFiles()
            .Select(k => new KeptBaseline(k.Version, FileSystem.Shared.FromUnsanitizedFullPath(k.File), ProvenanceOf(k.File), k.IsAlternate))
            .OrderBy(k => k.Version, StringComparer.Ordinal)
            .ThenBy(k => k.File.FileName, StringComparer.Ordinal)
            .ToArray();

    private IEnumerable<(string Version, string File, bool IsAlternate)> KeptFiles()
    {
        var folder = root().GetFullPath();
        if (!Directory.Exists(folder)) yield break;

        foreach (var file in Directory.EnumerateFiles(folder, Prefix + "*" + Suffix).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            var version = name[Prefix.Length..^Suffix.Length];
            yield return (version.Split(AlternateMark)[0], file, version.Contains(AlternateMark));
        }
    }

    private Hash? VouchedHash(string file)
    {
        var provenance = ProvenanceOf(file);
        if (provenance.Tier != BaselineTier.Vouched) return null;
        return ReadSidecar(file) ?? Hash.From(ulong.Parse(provenance.Xxh3, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Keeps the installed game's regulation as the baseline of <paramref name="live"/>'s version and returns it.
    /// The game folder is only read. The copy is vouched; it is recorded as listed by a Steam manifest when one lists it.
    /// <para>
    /// Whichever copy comes back is checked against <see cref="BuildIdentity.RegulationHash"/>, the digest the install
    /// was identified by. For a fresh adoption a different hash means the install changed between identifying and
    /// adopting. For a copy already kept it means the file it was taken from is not the one installed now: the version
    /// stamp is eight bytes and says nothing about the rest of a regulation, so two different files can carry the same
    /// one, and folding a merge onto the wrong one still yields output that claims the installed build. The exception
    /// is a validated copy, which the installed file replaces: the game's own file outranks a guess.
    /// </para>
    /// </summary>
    /// <exception cref="HealException">The regulation cannot be read, changed since it was identified, or differs from the kept baseline.</exception>
    public AbsolutePath Adopt(GameInstallation installation, BuildIdentity live)
    {
        var version = live.RegulationVersion
            ?? throw new HealException("The installed build has no regulation version, so there is no baseline to keep.");
        var identified = live.RegulationHash
            ?? throw new HealException("The installed build has no regulation hash, so a regulation cannot be checked against it.");
        var dest = PathOf(version);

        string? replaces = null;
        if (dest.FileExists)
        {
            var keptBlob = File.ReadAllBytes(dest.GetFullPath());
            var kept = Regulation.Sha256Hex(keptBlob);
            var existing = ProvenanceOf(dest.GetFullPath());
            if (existing.Tier == BaselineTier.Validated)
            {
                // Same bytes: the installed file confirms the copy. Other bytes: the installed file replaces it.
                if (string.Equals(kept, identified, StringComparison.OrdinalIgnoreCase))
                {
                    WriteProvenance(dest.GetFullPath(), ProvenanceFor(installation, keptBlob, replaces: null));
                    return dest;
                }
                replaces = kept;
            }
            else if (!string.Equals(kept, identified, StringComparison.OrdinalIgnoreCase))
            {
                throw new HealException(
                    $"{dest} was kept as the baseline for build {version}, but it is not the regulation now installed " +
                    $"(kept {Short(kept)}, installed {Short(identified)}) — verify the game's files in its launcher, or move that " +
                    "baseline aside so this build's real file can be adopted");
            }
            else
            {
                return dest;
            }
        }

        var blob = ReadInstalled(installation, identified);
        Keep(dest, blob, ProvenanceFor(installation, blob, replaces));
        return dest;
    }

    // The installed regulation, which must still be the file the build was identified by.
    private static byte[] ReadInstalled(GameInstallation installation, string identified)
    {
        var source = RegulationFile(installation);
        byte[] blob;
        try
        {
            blob = File.ReadAllBytes(source.GetFullPath());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new HealException($"can't read {source}: {exception.Message}");
        }

        var got = Regulation.Sha256Hex(blob);
        if (!string.Equals(got, identified, StringComparison.OrdinalIgnoreCase))
            throw new HealException(
                $"{source} changed between planning and adopting (identified {Short(identified)}, read {Short(got)}) — " +
                "run again so the heal plans against the file as it now stands");
        return blob;
    }

    /// <summary>
    /// Keeps the live build's regulation when the app has no copy of those bytes yet and the build is not one something
    /// other than Steam wrote: the first sight of a build may be the last, since a game update replaces the file. A
    /// hotfix that kept the build stamp goes beside the vouched copy as an alternate, never over it.
    /// Returns where it was kept, or null when nothing was to be kept or it could not be: this runs beside work that
    /// must not fail for want of a copy, so a failure is logged and the next identification tries again.
    /// </summary>
    /// <param name="stamped">The build recorded for the installation, null when there is none.</param>
    /// <param name="live">The build installed now.</param>
    public AbsolutePath? KeepBuild(GameInstallation installation, BuildIdentity? stamped, BuildIdentity live)
    {
        if (installation.Game is not IFromSoftGame { Formats.RegulationFileName: not null }) return null;
        if (live.RegulationVersion is not { } version || live.RegulationHash is not { } identified) return null;
        if (BuildIdentity.Classify(stamped, live) == BuildChangeKind.Tampered) return null;

        try
        {
            if (Provenance(version) is not { Tier: BaselineTier.Vouched }) return Adopt(installation, live);

            // The recorded SHA-256 is compared rather than the files rehashed: this runs on every plan.
            if (FindAll(version).Any(f => ProvenanceOf(f.GetFullPath()).Sha256.Equals(identified, StringComparison.OrdinalIgnoreCase))) return null;

            var blob = ReadInstalled(installation, identified);
            var provenance = ProvenanceFor(installation, blob, replaces: null);
            var alternate = AlternateOf(version, provenance);
            Keep(alternate, blob, provenance);
            return alternate;
        }
        catch (Exception exception) when (exception is HealException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The regulation of build {Version} could not be kept", version);
            return null;
        }
    }

    /// <summary>
    /// Keeps a regulation Steam itself shipped, such as another tool's copy of an older build, as the baseline of the
    /// build stamped inside it. Only a file whose SHA-1 is listed in a Steam depot manifest is accepted: a file that
    /// merely looks right is not kept here.
    /// </summary>
    /// <param name="expectedVersion">The build the caller believes the file is, or null to take whatever the file says.</param>
    /// <exception cref="HealException">The file cannot be read, is not in a Steam manifest, is not a regulation the game's key opens, or is another build than expected.</exception>
    public RegulationImport Import(GameInstallation installation, AbsolutePath file, string? expectedVersion)
    {
        byte[] blob;
        try
        {
            blob = File.ReadAllBytes(file.GetFullPath());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new HealException($"can't read {file}: {exception.Message}");
        }

        var sha1 = Sha1Value.From(SHA1.HashData(blob));
        var match = vouching?.Find(installation, sha1);
        if (match is null)
            throw new HealException(vouching is null
                ? $"{file} can't be checked against Steam's manifests: the app has no way to read them here."
                : $"{file} (SHA-1 {sha1.ToString().ToLowerInvariant()}) is not in any Steam depot manifest the app can read, so it can't be vouched for as the game's own file.");

        string version;
        try
        {
            version = Regulation.Version(Regulation.Unpack(blob, GameOf(installation).Formats));
        }
        catch (Exception exception) when (exception is RegulationException or AesException or DcxException or KrakenException)
        {
            throw new HealException($"{file} is not a regulation this game's key opens: {exception.Message}");
        }
        if (expectedVersion is not null && !version.Equals(expectedVersion, StringComparison.Ordinal))
            throw new HealException($"{file} is regulation build {version}, not {expectedVersion}.");

        var provenance = NewProvenance(BaselineSource.SteamManifest, blob, match, file.GetFullPath());
        var dest = PathOf(version);
        var sha256 = Regulation.Sha256Hex(blob);
        foreach (var kept in FindAll(version))
        {
            if (!Regulation.Sha256Hex(File.ReadAllBytes(kept.GetFullPath())).Equals(sha256, StringComparison.Ordinal)) continue;

            // The same bytes are already here. A validated copy has just been confirmed by Steam.
            var existing = ProvenanceOf(kept.GetFullPath());
            if (existing.Tier == BaselineTier.Vouched) return new RegulationImport(kept, version, ImportOutcome.AlreadyKept, existing);
            WriteProvenance(kept.GetFullPath(), provenance);
            return new RegulationImport(kept, version, ImportOutcome.ReplacedValidated, provenance);
        }

        if (!dest.FileExists)
        {
            Keep(dest, blob, provenance);
            return new RegulationImport(dest, version, ImportOutcome.Kept, provenance);
        }

        var primary = ProvenanceOf(dest.GetFullPath());
        if (primary.Tier == BaselineTier.Validated)
        {
            var replacing = provenance with { Replaces = primary.Sha256 };
            Keep(dest, blob, replacing);
            return new RegulationImport(dest, version, ImportOutcome.ReplacedValidated, replacing);
        }

        var alternate = AlternateOf(version, provenance);
        Keep(alternate, blob, provenance);
        return new RegulationImport(alternate, version, ImportOutcome.KeptAlongside, provenance);
    }

    /// <summary>A Steam depot manifest of this install lists the file, so <see cref="Import"/> keeps it vouched.</summary>
    public bool ListedBySteam(GameInstallation installation, byte[] blob)
        => vouching?.Find(installation, Sha1Value.From(SHA1.HashData(blob))) is not null;

    /// <summary>
    /// What would be recorded for a regulation that nothing outside the file vouches for but that passed the checks of a
    /// vanilla (<see cref="VanillaValidation"/>). Validated, so it may be an ancestor but never vouches for an archive.
    /// </summary>
    /// <param name="source">Where the file was found: a library archive or a file the user pointed at.</param>
    /// <param name="origin">Which archive member or path it was read from.</param>
    /// <param name="validation">What the checks found.</param>
    public RegulationProvenance ValidatedProvenance(byte[] blob, BaselineSource source, string origin, string validation)
        => new(BaselineTier.Validated, source,
            Convert.ToHexStringLower(SHA1.HashData(blob)),
            Regulation.Sha256Hex(blob),
            blob.xxHash3().Value.ToString("x16"),
            _time.GetUtcNow(),
            OriginalPath: origin,
            Validation: validation);

    /// <summary>
    /// Keeps a validated regulation as the baseline of <paramref name="version"/>. Never replaces a copy already kept,
    /// of either tier: a file the app has served as an ancestor stays the one, and a vouched file takes a validated
    /// one's place only through <see cref="Import"/> or <see cref="Adopt"/>.
    /// </summary>
    /// <returns>The kept file, which is the existing one when the version already had a baseline.</returns>
    public AbsolutePath KeepValidated(string version, byte[] blob, RegulationProvenance provenance)
    {
        if (provenance.Tier != BaselineTier.Validated)
            throw new ArgumentException("Only a validated regulation is kept through here.", nameof(provenance));

        var dest = PathOf(version);
        if (dest.FileExists) return dest;
        Keep(dest, blob, provenance);
        return dest;
    }

    // A copy out of the install: vouched, and recorded as Steam's when a manifest lists it.
    private RegulationProvenance ProvenanceFor(GameInstallation installation, byte[] blob, string? replaces)
    {
        var match = vouching?.Find(installation, Sha1Value.From(SHA1.HashData(blob)));
        var source = match is null ? BaselineSource.InstalledGame : BaselineSource.SteamManifest;
        return NewProvenance(source, blob, match, originalPath: null) with
        {
            ManifestMatched = vouching is null ? null : match is not null,
            Replaces = replaces,
        };
    }

    private RegulationProvenance NewProvenance(BaselineSource source, byte[] blob, ManifestMatch? match, string? originalPath)
        => new(BaselineTier.Vouched, source,
            Convert.ToHexStringLower(SHA1.HashData(blob)),
            Regulation.Sha256Hex(blob),
            blob.xxHash3().Value.ToString("x16"),
            _time.GetUtcNow(),
            ManifestId: match?.ManifestId.Value,
            ManifestMatched: match is null ? null : true,
            OriginalPath: originalPath);

    // The provenance and hash go in first and the file last, under another name and moved in, so a half-written file
    // never answers Find and a file that answers it always has its provenance.
    private static void Keep(AbsolutePath dest, byte[] blob, RegulationProvenance provenance)
    {
        Directory.CreateDirectory(dest.Parent.GetFullPath());
        var path = dest.GetFullPath();
        WriteProvenance(path, provenance);
        File.WriteAllText(path + SidecarSuffix, blob.xxHash3().Value.ToString("x16"));
        var partial = path + ".partial";
        File.WriteAllBytes(partial, blob);
        File.Move(partial, path, overwrite: true);
    }

    private static void WriteProvenance(string file, RegulationProvenance provenance)
        => File.WriteAllText(file + ProvenanceSuffix, provenance.ToJson());

    // A baseline with no provenance was copied out of an install before provenance existed, so it counts as that and
    // gets its provenance written. If the folder can't be written the answer is still right and is worked out again.
    private RegulationProvenance ProvenanceOf(string file)
    {
        var sidecar = file + ProvenanceSuffix;
        if (File.Exists(sidecar))
        {
            try
            {
                return RegulationProvenance.Parse(File.ReadAllText(sidecar));
            }
            catch (JsonException exception)
            {
                throw new HealException($"{sidecar} can't be read ({exception.Message}); the baseline is not used until that is fixed.");
            }
        }

        var blob = File.ReadAllBytes(file);
        var legacy = NewProvenance(BaselineSource.InstalledGame, blob, match: null, originalPath: null);
        try
        {
            WriteProvenance(file, legacy);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(exception, "The provenance of {File} could not be written", file);
        }
        return legacy;
    }

    private AbsolutePath PathOf(string version)
    {
        if (version.Length == 0 || !version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            throw new ArgumentException($"'{version}' cannot be a regulation version: it has to be usable as part of a file name.", nameof(version));
        return root() / (Prefix + version + Suffix);
    }

    private AbsolutePath AlternateOf(string version, RegulationProvenance provenance)
        => root() / (Prefix + version + AlternateMark + provenance.Sha1[..12] + Suffix);

    private static IFromSoftGame GameOf(GameInstallation installation)
        => installation.Game as IFromSoftGame
           ?? throw new InvalidOperationException($"{installation.Game.DisplayName} is not a FromSoft game, so it has no regulation to keep.");

    private static AbsolutePath RegulationFile(GameInstallation installation)
    {
        var game = GameOf(installation);
        var fileName = game.Formats.RegulationFileName
            ?? throw new InvalidOperationException($"{game.DisplayName} has no regulation file to keep.");
        return FromSoftPaths.GameFolder(game.Layout, installation) / fileName;
    }

    private static Hash? ReadSidecar(string file)
    {
        var sidecar = file + SidecarSuffix;
        if (!File.Exists(sidecar)) return null;
        return ulong.TryParse(File.ReadAllText(sidecar).Trim(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
            ? Hash.From(value)
            : null;
    }

    private static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;
}

/// <summary>A regulation the app keeps.</summary>
/// <param name="Version">The build stamp it carries.</param>
/// <param name="File">Where it is kept.</param>
/// <param name="Provenance">Where it came from and how far it is trusted.</param>
/// <param name="IsAlternate">A second vouched file of a build already kept, which a merge uses only when it is nearer the mods' rows.</param>
public sealed record KeptBaseline(string Version, AbsolutePath File, RegulationProvenance Provenance, bool IsAlternate);
