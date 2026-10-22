using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.RegularExpressions;
using FluentAssertions;
using NexusMods.Games.FromSoft.Formats;

namespace NexusMods.Games.FromSoft.Tests.Formats;

/// <summary>
/// Decodes a Kraken member out of a real mod archive. Never part of the default run: it needs a checkout of the
/// erm tool and the mod archive it pinned, and no mod or game file is checked in. The archive is found the way
/// that tool finds it, through the mod id in its lock file, because asset file names change on every repin.
/// </summary>
public partial class KrakenRealArchiveTests
{
    private const string RootVariable = "SILVER_ERM_ROOT";
    private const string ModId = "clevers-moveset";
    private const string Member = "chr/c0000.behbnd.dcx";

    private const int DcxHeaderSize = 0x4C;

    [Fact]
    public void AKrakenMemberOfTheMovesetPackageDecodesToABinder()
    {
        var root = Environment.GetEnvironmentVariable(RootVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(root), $"{RootVariable} is not set");

        var lockFile = Path.Combine(root!, "mods.lock.toml");
        Assert.SkipUnless(File.Exists(lockFile), $"{lockFile} does not exist");

        var asset = PinnedAsset(File.ReadAllText(lockFile), ModId);
        Assert.SkipWhen(asset is null, $"mods.lock.toml has no archive pinned for {ModId}");

        var archivePath = Path.Combine(root!, "vendor", asset!);
        Assert.SkipUnless(File.Exists(archivePath), $"vendor archive missing: {asset}");
        Assert.SkipUnless(asset!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase), $"{asset} is not a zip");

        using var zip = ZipFile.OpenRead(archivePath);
        var entry = zip.Entries.SingleOrDefault(e => e.FullName == Member || e.FullName.EndsWith("/" + Member, StringComparison.Ordinal));
        Assert.SkipWhen(entry is null, $"{asset} has no member ending in {Member}");

        using var stream = entry!.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var dcx = memory.ToArray();

        Assert.SkipUnless(dcx.AsSpan(0, 4).SequenceEqual("DCX\0"u8), "the member is not a DCX container");
        Assert.SkipUnless(dcx.AsSpan(0x28, 4).SequenceEqual("KRAK"u8), "the member is not Kraken-compressed");

        var uncompressed = (int)BinaryPrimitives.ReadUInt32BigEndian(dcx.AsSpan(0x1C));
        var compressed = (int)BinaryPrimitives.ReadUInt32BigEndian(dcx.AsSpan(0x20));

        var payload = Kraken.Decompress(dcx.AsSpan(DcxHeaderSize, compressed), uncompressed);

        payload.Should().HaveCount(uncompressed);
        payload.AsSpan(0, 4).SequenceEqual("BND4"u8).Should().BeTrue("a behaviour binder starts with its magic");
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
