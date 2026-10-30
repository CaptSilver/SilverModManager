using System.Security.Cryptography;
using FluentAssertions;
using SilverModManager.Wabbajack.Acquire.Hosts;

namespace SilverModManager.Wabbajack.Tests.Acquire.Hosts;

public class Aes128CtrStreamTests
{
    /// <summary>One block at a time through ECB, so it shares no code with the stream under test.</summary>
    internal static byte[] Reference(byte[] key, byte[] nonce, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var output = new byte[data.Length];
        var counterBlock = new byte[16];
        var keystream = new byte[16];
        for (var block = 0; block * 16 < data.Length; block++)
        {
            nonce.CopyTo(counterBlock, 0);
            for (var i = 0; i < 8; i++) counterBlock[15 - i] = (byte)((ulong)block >> (8 * i));
            aes.EncryptEcb(counterBlock, keystream, PaddingMode.None);
            for (var i = 0; i < 16 && block * 16 + i < data.Length; i++) output[block * 16 + i] = (byte)(data[block * 16 + i] ^ keystream[i]);
        }
        return output;
    }

    private static async Task<byte[]> ReadAll(Stream stream, int chunk)
    {
        var result = new MemoryStream();
        var buffer = new byte[chunk];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0) result.Write(buffer, 0, read);
        return result.ToArray();
    }

    [Fact]
    public async Task MatchesTheNistAes128CtrVector()
    {
        // NIST SP 800-38A F.5.1. The counter block f0f1f2f3f4f5f6f7 f8f9fafbfcfdfeff splits into the nonce and a
        // 64-bit block counter, which is how the stream builds its counter blocks.
        var key = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");
        var nonce = Convert.FromHexString("f0f1f2f3f4f5f6f7");
        var plain = Convert.FromHexString("6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e5130c81c46a35ce411e5fbc1191a0a52eff69f2445df4f9b17ad2b417be66c3710");
        var expected = "874d6191b620e3261bef6864990db6ce9806f66b7970fdff8617187bb9fffdff5ae4df3edbd5d35e5b4f09020db03eab1e031dda2fbe03d1792170a0f3009cee";

        await using var stream = new Aes128CtrStream(new MemoryStream(plain), key, nonce, firstBlock: 0xf8f9fafbfcfdfeffUL);

        Convert.ToHexString(await ReadAll(stream, 64)).ToLowerInvariant().Should().Be(expected);
    }

    [Fact]
    public async Task TheReferenceHelperAgreesWithTheNistVectorToo()
    {
        var key = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");
        var nonce = Convert.FromHexString("f0f1f2f3f4f5f6f7");
        var data = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        await using var stream = new Aes128CtrStream(new MemoryStream(data), key, nonce);

        (await ReadAll(stream, 100)).Should().Equal(Reference(key, nonce, data));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(4096)]
    [InlineData(100_000)]
    public async Task TheKeystreamCarriesOverReadsThatAreNotBlockAligned(int chunk)
    {
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i + 3)).ToArray();
        var nonce = Enumerable.Range(0, 8).Select(i => (byte)(255 - i)).ToArray();
        var data = new byte[300_007];
        new Random(1).NextBytes(data);

        await using var stream = new Aes128CtrStream(new MemoryStream(data), key, nonce);

        (await ReadAll(stream, chunk)).Should().Equal(Reference(key, nonce, data));
    }

    [Fact]
    public async Task ReadingThroughTheStreamTwiceRestoresThePlaintext()
    {
        var key = new byte[16];
        var nonce = new byte[8];
        var data = new byte[5000];
        new Random(2).NextBytes(data);

        await using var once = new Aes128CtrStream(new MemoryStream(data), key, nonce);
        var cipher = await ReadAll(once, 1000);
        await using var twice = new Aes128CtrStream(new MemoryStream(cipher), key, nonce);

        (await ReadAll(twice, 333)).Should().Equal(data);
    }

    [Fact]
    public async Task TheStreamIsNotSeekableOrWritable()
    {
        await using var stream = new Aes128CtrStream(new MemoryStream(), new byte[16], new byte[8]);

        stream.CanSeek.Should().BeFalse();
        stream.CanWrite.Should().BeFalse();
        stream.CanRead.Should().BeTrue();
    }
}
