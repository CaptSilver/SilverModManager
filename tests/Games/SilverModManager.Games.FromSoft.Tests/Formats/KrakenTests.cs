using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FluentAssertions;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Formats.Native;
using SilverModManager.Sdk.Native;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;

namespace SilverModManager.Games.FromSoft.Tests.Formats;

public class KrakenTests
{
    [Fact]
    public void TheBundledLibraryIsCopiedBesideTheAssemblyAndExportsTheEntryPoint()
    {
        var path = Path.Combine(Path.GetDirectoryName(typeof(Kraken).Assembly.Location)!, "runtimes", BundledNativeLibrary.RuntimeIdentifiers()[^1], "native", "libooz.so");
        File.Exists(path).Should().BeTrue("the project copies the built library to the output folder");

        NativeLibrary.TryLoad(path, out var handle).Should().BeTrue();
        try
        {
            NativeLibrary.TryGetExport(handle, "ooz_decompress_seekchunked", out _).Should().BeTrue();
        }
        finally
        {
            NativeLibrary.Free(handle);
        }

        // Going through the P/Invoke proves the resolver finds the same file.
        Kraken.Decompress([], 0).Should().BeEmpty();
    }

    [Fact]
    public void ARefusalNamesTheScriptThatBuildsTheLibrary()
    {
        LibOozResolver.NotFoundMessage(["/app/runtimes/linux-x64/native/libooz.so"]).Should().Contain("scripts/build-ooz.sh").And.Contain("/app/runtimes/linux-x64/native/libooz.so");
    }

    [Fact]
    public void BytesThatAreNotAKrakenStreamAreRefusedAsAFailedDecode()
    {
        var act = () => Kraken.Decompress("not a kraken stream"u8, 4096);
        act.Should().Throw<KrakenException>().WithMessage("*decode failed*4096*");
    }

    [Fact]
    public void AnUncompressedSizeAboveTheCapIsRefusedBeforeAnythingIsAllocated()
    {
        var act = () => Kraken.Decompress("irrelevant"u8, Kraken.MaxUncompressedSize + 1);
        act.Should().Throw<KrakenException>().WithMessage("*refusing to allocate*");
    }

    [Fact]
    public void ANegativeUncompressedSizeIsRefused()
    {
        var act = () => Kraken.Decompress("irrelevant"u8, -1);
        act.Should().Throw<KrakenException>();
    }

    [Fact]
    public void AStoredSingleChunkStreamDecodesToItsPayload()
    {
        var payload = KrakenFixture.Payload(1000);
        Kraken.Decompress(KrakenFixture.Stored(payload), payload.Length).Should().Equal(payload);
    }

    [Fact]
    public void AStreamOfTwoChunksDecodesToItsPayload()
    {
        var payload = KrakenFixture.Payload(300 * 1024);
        var stream = KrakenFixture.Stored(payload);
        stream.Length.Should().Be(payload.Length + 4, "two chunks, two header bytes each");
        Kraken.Decompress(stream, payload.Length).Should().Equal(payload);
    }

    [Fact]
    public void AChunkShorterThanItsDeclaredSizeIsRefusedAsStalled()
    {
        var payload = KrakenFixture.Payload(1000);
        var stream = KrakenFixture.Stored(payload)[..(2 + 990)];
        var act = () => Kraken.Decompress(stream, payload.Length);
        act.Should().Throw<KrakenException>().WithMessage("*decode stalled*");
    }

    [Fact]
    public void AStreamThatEndsBeforeTheDeclaredSizeIsRefused()
    {
        var payload = KrakenFixture.Payload(100);
        var act = () => Kraken.Decompress(KrakenFixture.Stored(payload), 200);
        act.Should().Throw<KrakenException>().WithMessage("*decode stalled*expected 200*", "a stored chunk waits for the bytes the caller asked for, which the stream does not hold");
    }

    [Theory]
    [InlineData(-1, "decode failed")]
    [InlineData(-2, "decode stalled")]
    [InlineData(-7, "returned sentinel -7")]
    [InlineData(5, "produced 5 bytes")]
    public void EveryResultButTheExactSizeIsNamed(int returned, string reason)
    {
        var act = () => Kraken.EnsureExact(returned, 10);
        act.Should().Throw<KrakenException>().WithMessage($"*{reason}*expected 10*");
    }

    [Fact]
    public void TheExactSizeIsAccepted()
    {
        var act = () => Kraken.EnsureExact(10, 10);
        act.Should().NotThrow();
    }

    /// <summary>
    /// The SHA-256 of each committed DCX container's decoded payload, computed outside this code base (zlib and the
    /// zstd tool). The same file must give the same digest on every architecture. These containers are Deflate and
    /// Zstandard; no Kraken container is committed (game and mod files are not), so they do not exercise libooz.
    /// </summary>
    public static TheoryData<string, string> DcxFixtureDigests => new()
    {
        { "dflt_payload.dcx", "239f59ed55e737c77147cf55ad0c1b030b6d7ee748a7426952f9b852d5a935e5" },
        { "planner_high_01_common.tpf.dcx", "54c605f1b45fa2a23037445f6e92c0c95b94b21755bfdbb9682cfc82b535e6ca" },
        { "planner_high_item.msgbnd.dcx", "8e62608129bad3de8332e6ac97136b72896aa18e2a95437596e87681d8a90846" },
        { "planner_low_01_common.tpf.dcx", "6a4e571e3376190db383d8e2f9f40261c0adc4359f540e30126fb02723695ed4" },
        { "planner_low_item.msgbnd.dcx", "c87b030b88c52102ac649de38efc1c7c93bc1e594583580452e01a078c0f490b" },
        { "zstd_pinned.dcx", "f63f3ebc8a5c414d9bf393f7a72007f6aea2956181dcf87a7748ed47bf18ef39" },
    };

    [Theory]
    [MemberData(nameof(DcxFixtureDigests))]
    public void EveryCommittedDcxFixtureDecodesToTheRecordedDigest(string name, string digest)
    {
        var decoded = Dcx.Read(GoldenFilesTests.Read(name), EldenRingGame.Definition.Formats);

        Convert.ToHexStringLower(SHA256.HashData(decoded)).Should().Be(digest);
    }

    [Fact]
    public void EveryCommittedDcxFixtureHasARecordedDigest()
    {
        var committed = Directory.EnumerateFiles(GoldenFilesTests.Folder, "*.dcx").Select(Path.GetFileName);
        committed.Should().BeEquivalentTo(DcxFixtureDigests.Select(row => row.Data.Item1));
    }
}
