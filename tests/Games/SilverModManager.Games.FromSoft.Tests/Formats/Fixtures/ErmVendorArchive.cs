using System.IO.Compression;
using System.Text.RegularExpressions;

namespace NexusMods.Games.FromSoft.Tests.Formats.Fixtures;

/// <summary>
/// Finds a member of a real mod archive inside a checkout of the erm tool, for the opt-in tests that need real
/// bytes. The archive is found the way that tool finds it, through the mod id in its lock file, because asset file
/// names change on every repin. Anything missing skips the calling test with a reason naming what was missing.
/// </summary>
public static partial class ErmVendorArchive
{
    public const string RootVariable = "SILVER_ERM_ROOT";

    /// <summary>
    /// The bytes of the first of <paramref name="memberSuffixes"/> that the archive pinned for
    /// <paramref name="modId"/> holds.
    /// </summary>
    public static byte[] ReadPinnedMember(string modId, params string[] memberSuffixes)
    {
        var root = Environment.GetEnvironmentVariable(RootVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(root), $"{RootVariable} is not set");

        var lockFile = Path.Combine(root!, "mods.lock.toml");
        Assert.SkipUnless(File.Exists(lockFile), $"{lockFile} does not exist");

        var asset = PinnedAsset(File.ReadAllText(lockFile), modId);
        Assert.SkipWhen(asset is null, $"mods.lock.toml has no archive pinned for {modId}");

        var archivePath = Path.Combine(root!, "vendor", asset!);
        Assert.SkipUnless(File.Exists(archivePath), $"vendor archive missing: {asset}");
        Assert.SkipUnless(asset!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase), $"{asset} is not a zip");

        using var zip = ZipFile.OpenRead(archivePath);
        var entry = memberSuffixes
            .Select(m => zip.Entries.SingleOrDefault(e => e.FullName == m || e.FullName.EndsWith("/" + m, StringComparison.Ordinal)))
            .FirstOrDefault(e => e is not null);
        Assert.SkipWhen(entry is null, $"{asset} has no member ending in any of {string.Join(", ", memberSuffixes)}");

        using var stream = entry!.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string? PinnedAsset(string lockText, string modId)
    {
        var section = Regex.Match(lockText, $@"^\[{Regex.Escape(modId)}\]\s*$(?<body>.*?)(?=^\[|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        if (!section.Success) return null;
        var asset = AssetLine().Match(section.Groups["body"].Value);
        return asset.Success ? asset.Groups["asset"].Value : null;
    }

    [GeneratedRegex(@"^asset\s*=\s*""(?<asset>[^""]*)""", RegexOptions.Multiline)]
    private static partial Regex AssetLine();
}
