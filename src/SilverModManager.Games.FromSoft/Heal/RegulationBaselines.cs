using System.Globalization;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Formats;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft.Heal;

/// <summary>
/// The vanilla regulations kept under the data root, one per build, as <c>regulation-&lt;version&gt;.bin</c>. A kept
/// baseline is the game's own regulation of that version, the file a param merge compares its contributors against,
/// so nothing but a file copied out of an install may ever land here. A baseline is never overwritten: a second file
/// under one version is refused, not replaced, and old versions stay so a merge built against them can be reproduced.
/// </summary>
/// <param name="root">The folder the baselines live in. Resolved on every call, so a changed data root is followed.</param>
public sealed class RegulationBaselines(Func<AbsolutePath> root)
{
    private const string Prefix = "regulation-";
    private const string Suffix = ".bin";
    private const string SidecarSuffix = ".xxh3";

    /// <summary>The kept baseline of <paramref name="version"/>, or null when none was adopted. Looks at the folder and nothing else.</summary>
    public AbsolutePath? Find(string version)
    {
        var file = PathOf(version);
        return file.FileExists ? file : null;
    }

    /// <summary>
    /// Every kept version with the xxHash3 of its file. The hash is the one written beside the baseline when it was
    /// adopted; only a file that has none, such as one placed by hand, is hashed here.
    /// </summary>
    public IReadOnlyDictionary<string, Hash> Known()
    {
        var folder = root().GetFullPath();
        var known = new Dictionary<string, Hash>(StringComparer.Ordinal);
        if (!Directory.Exists(folder)) return known;

        foreach (var file in Directory.EnumerateFiles(folder, Prefix + "*" + Suffix).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            known[name[Prefix.Length..^Suffix.Length]] = ReadSidecar(file) ?? File.ReadAllBytes(file).xxHash3();
        }
        return known;
    }

    /// <summary>
    /// Keeps the installed game's regulation as the baseline of <paramref name="live"/>'s version and returns it.
    /// The game folder is only read.
    /// <para>
    /// Whichever copy comes back is checked against <see cref="BuildIdentity.RegulationHash"/>, the digest the install
    /// was identified by. For a fresh adoption a different hash means the install changed between identifying and
    /// adopting. For a copy already kept it means the file it was taken from is not the one installed now: the version
    /// stamp is eight bytes and says nothing about the rest of a regulation, so two different files can carry the same
    /// one, and folding a merge onto the wrong one still yields output that claims the installed build.
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

        if (dest.FileExists)
        {
            var kept = Regulation.Sha256Hex(File.ReadAllBytes(dest.GetFullPath()));
            if (!string.Equals(kept, identified, StringComparison.OrdinalIgnoreCase))
                throw new HealException(
                    $"{dest} was kept as the baseline for build {version}, but it is not the regulation now installed " +
                    $"(kept {Short(kept)}, installed {Short(identified)}) — verify the game's files in its launcher, or move that " +
                    "baseline aside so this build's real file can be adopted");
            return dest;
        }

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

        Directory.CreateDirectory(dest.Parent.GetFullPath());
        // Written under another name and moved in, so a half-written file never answers Find.
        var partial = dest.GetFullPath() + ".partial";
        File.WriteAllBytes(partial, blob);
        File.Move(partial, dest.GetFullPath());
        File.WriteAllText(dest.GetFullPath() + SidecarSuffix, blob.xxHash3().Value.ToString("x16"));
        return dest;
    }

    private AbsolutePath PathOf(string version)
    {
        if (version.Length == 0 || !version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            throw new ArgumentException($"'{version}' cannot be a regulation version: it has to be usable as part of a file name.", nameof(version));
        return root() / (Prefix + version + Suffix);
    }

    private static AbsolutePath RegulationFile(GameInstallation installation)
    {
        if (installation.Game is not IFromSoftGame game)
            throw new InvalidOperationException($"{installation.Game.DisplayName} is not a FromSoft game, so it has no regulation to keep.");
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
