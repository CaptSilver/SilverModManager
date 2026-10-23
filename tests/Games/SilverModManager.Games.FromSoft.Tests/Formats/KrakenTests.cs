using System.Runtime.InteropServices;
using FluentAssertions;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Formats.Native;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;

namespace SilverModManager.Games.FromSoft.Tests.Formats;

public class KrakenTests
{
    [Fact]
    public void TheBundledLibraryIsCopiedBesideTheAssemblyAndExportsTheEntryPoint()
    {
        var path = Path.Combine(Path.GetDirectoryName(typeof(Kraken).Assembly.Location)!, "runtimes", "linux-x64", "native", "libooz.so");
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
        LibOozResolver.NotFoundMessage.Should().Contain("scripts/build-ooz.sh");
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
}
