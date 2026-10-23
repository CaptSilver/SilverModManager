using System.Security.Cryptography;
using FluentAssertions;
using NexusMods.Abstractions.Steam;
using NexusMods.Abstractions.Steam.Values;
using NexusMods.Paths;
using NexusMods.Sdk.Hashes;
using SteamKit2;
using Xunit;

namespace NexusMods.Networking.Steam.Tests;

public sealed class LocalManifestReaderTests : IDisposable
{
    private static readonly DepotId Depot = DepotId.From(489831);
    private static readonly ManifestId Manifest = ManifestId.From(77);

    private readonly AbsolutePath _dir = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory) / $"manifest-reader-{Guid.NewGuid():N}";
    private readonly LocalManifestReader _reader = new();

    public LocalManifestReaderTests() => _dir.CreateDirectory();
    public void Dispose() => _dir.DeleteDirectory(true);

    private AbsolutePath ManifestPath => _dir / $"{Depot.Value}_{Manifest.Value}.manifest";

    [Fact]
    public void Read_PlainManifest_YieldsPathSizeAndSha1PerFile()
    {
        byte[] a = [1, 2, 3];
        byte[] b = [4, 5, 6, 7];
        DepotManifestFixture.Write(ManifestPath, Depot, Manifest, [("a.bin", a), ("sub/b.bin", b)]);

        var result = _reader.Read(ManifestPath).Should().BeOfType<LocalManifestResult.Parsed>().Subject;

        result.Manifest.DepotId.Should().Be(Depot);
        result.Manifest.ManifestId.Should().Be(Manifest);
        result.Manifest.Files.Should().HaveCount(2);
        var file = result.Manifest.Files.Single(f => f.Path == "sub/b.bin");
        file.Size.Value.Should().Be(4);
        file.Hash.Should().Be(Sha1Value.From(SHA1.HashData(b)));
    }

    [Fact]
    public void Read_EncryptedFilenames_ReturnsNeedsDepotKeyWithoutNames()
    {
        DepotManifestFixture.Write(ManifestPath, Depot, Manifest, [("a.bin", [1])], encryptFilenames: true);

        var result = _reader.Read(ManifestPath);

        result.Should().Be(new LocalManifestResult.NeedsDepotKey(Depot, Manifest));
    }

    [Fact]
    public void Read_TruncatedFile_ReturnsUnreadable()
    {
        DepotManifestFixture.Write(ManifestPath, Depot, Manifest, [("a.bin", [1, 2, 3])]);
        var bytes = File.ReadAllBytes(ManifestPath.ToString());
        var payloadLength = BitConverter.ToUInt32(bytes, 4);
        File.WriteAllBytes(ManifestPath.ToString(), bytes[..(int)(8 + payloadLength)]);

        _reader.Read(ManifestPath).Should().BeOfType<LocalManifestResult.Unreadable>();
    }

    [Fact]
    public void Read_WrongMagic_ReturnsUnreadable()
    {
        File.WriteAllBytes(ManifestPath.ToString(), [0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0, 1, 2, 3, 4]);

        _reader.Read(ManifestPath).Should().BeOfType<LocalManifestResult.Unreadable>();
    }

    [Fact]
    public void Read_MissingFile_ReturnsUnreadable()
    {
        _reader.Read(ManifestPath).Should().BeOfType<LocalManifestResult.Unreadable>();
    }

    [Fact]
    public void Read_DirectoryAndSymlinkEntries_AreDropped_ZeroSizeFilesAreKept()
    {
        DepotManifestFixture.Write(ManifestPath, Depot, Manifest,
        [
            new DepotManifestFixture.Entry("real.bin", [1, 2]),
            new DepotManifestFixture.Entry("empty.txt", []),
            new DepotManifestFixture.Entry("script.vdf", [3], EDepotFileFlag.InstallScript),
            new DepotManifestFixture.Entry("Data", [], EDepotFileFlag.Directory),
            new DepotManifestFixture.Entry("link.so", [], EDepotFileFlag.Symlink, "real.bin"),
            new DepotManifestFixture.Entry("link2.so", [], 0, "real.bin"),
        ]);

        var result = _reader.Read(ManifestPath).Should().BeOfType<LocalManifestResult.Parsed>().Subject;

        result.Manifest.Files.Select(f => f.Path.ToString()).Should().BeEquivalentTo("real.bin", "empty.txt", "script.vdf");
    }

    [Fact]
    public void Read_BackslashSeparatedNames_YieldForwardSlashPaths()
    {
        DepotManifestFixture.Write(ManifestPath, Depot, Manifest, [(@"Data\Skyrim.esm", [1])]);

        var result = _reader.Read(ManifestPath).Should().BeOfType<LocalManifestResult.Parsed>().Subject;

        result.Manifest.Files.Single().Path.ToString().Should().Be("Data/Skyrim.esm");
    }
}
