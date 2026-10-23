using System.Security.Cryptography;
using FluentAssertions;
using NexusMods.Abstractions.Steam.Values;
using NexusMods.Paths;
using SteamKit2;
using Xunit;

namespace NexusMods.Networking.Steam.Tests;

public sealed class DepotManifestFixtureTests : IDisposable
{
    private readonly AbsolutePath _dir = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory) / $"depot-fixture-{Guid.NewGuid():N}";

    public DepotManifestFixtureTests() => _dir.CreateDirectory();
    public void Dispose() => _dir.DeleteDirectory(true);

    [Fact]
    public void LoadFromFile_ReadsASynthesizedDepotCacheManifest()
    {
        var path = _dir / "489831_1234.manifest";
        byte[] skyrim = [1, 2, 3, 4, 5];
        DepotManifestFixture.Write(path, DepotId.From(489831), ManifestId.From(1234), [(@"Data\Skyrim.esm", skyrim), ("Skyrim.exe", [9, 9])]);

        var manifest = DepotManifest.LoadFromFile(path.ToString());

        manifest.Should().NotBeNull();
        manifest!.DepotID.Should().Be(489831);
        manifest.ManifestGID.Should().Be(1234);
        manifest.Files!.Should().HaveCount(2);
        var esm = manifest.Files!.Single(f => f.FileName.Replace('\\', '/') == "Data/Skyrim.esm");
        esm.TotalSize.Should().Be(5);
        esm.FileHash.Should().Equal(SHA1.HashData(skyrim));

        var bytes = File.ReadAllBytes(path.ToString());
        BitConverter.ToUInt32(bytes, 0).Should().Be(DepotManifestFixture.PayloadMagic);
        BitConverter.ToUInt32(bytes, bytes.Length - 4).Should().Be(DepotManifestFixture.EndMagic);

        // SteamKit turns separators into '/' on load, so only the raw bytes show what was written.
        bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(@"Data\Skyrim.esm")).Should().BeGreaterThanOrEqualTo(0);
    }
}
