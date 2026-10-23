using FluentAssertions;
using NexusMods.Games.FromSoft.Formats;

namespace NexusMods.Games.FromSoft.Tests.Formats;

public class AesCbcTests
{
    // NIST SP 800-38A F.2.5/F.2.6 (CBC-AES256).
    private static readonly byte[] NistKey = Convert.FromHexString("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4");
    private static readonly byte[] NistIv = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
    private static readonly byte[] NistPlain = Convert.FromHexString(
        "6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e51" +
        "30c81c46a35ce411e5fbc1191a0a52eff69f2445df4f9b17ad2b417be66c3710");
    private static readonly byte[] NistCipher = Convert.FromHexString(
        "f58c4c04d6e5f1ba779eabfb5f7bfbd69cfc4e967edb808d679f777bc6702c7d" +
        "39f23369a9d9bacfa530e26304231461b2eb05e2c39be9fcda6c19078c6a9d1b");

    // NIST SP 800-38A F.1.1 (ECB-AES128) and FIPS-197 C.1.
    private static readonly byte[] Ecb128Key = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");
    private static readonly byte[] Ecb128Plain = NistPlain;
    private static readonly byte[] Ecb128Cipher = Convert.FromHexString(
        "3ad77bb40d7a3660a89ecaf32466ef97f5d3d58503b9699de785895a96fdbaaf" +
        "43b1cd7f598ece23881b00e3ed0306887b0c785e27e8ad3f8223207104725dd4");
    private static readonly byte[] FipsKey = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
    private static readonly byte[] FipsPlain = Convert.FromHexString("00112233445566778899aabbccddeeff");
    private static readonly byte[] FipsCipher = Convert.FromHexString("69c4e0d86a7b0430d8cdb78070b4c55a");

    [Fact]
    public void EncryptMatchesTheNistVector() => AesCbc.EncryptCbc(NistKey, NistIv, NistPlain).Should().Equal(NistCipher);

    [Fact]
    public void DecryptMatchesTheNistVector() => AesCbc.DecryptCbc(NistKey, NistIv, NistCipher).Should().Equal(NistPlain);

    [Fact]
    public void ARoundTripOverManyBlocksSurvives()
    {
        // Every block after the first chains on the previous ciphertext block, so a chaining bug needs several blocks to show.
        var data = Enumerable.Range(0, 256 * 8).Select(i => (byte)i).ToArray();
        AesCbc.DecryptCbc(NistKey, NistIv, AesCbc.EncryptCbc(NistKey, NistIv, data)).Should().Equal(data);
    }

    [Fact]
    public void TheIvChangesTheCiphertext() => AesCbc.EncryptCbc(NistKey, new byte[16], NistPlain).Should().NotEqual(NistCipher);

    [Fact]
    public void EmptyInputGivesEmptyOutput()
    {
        AesCbc.EncryptCbc(NistKey, NistIv, []).Should().BeEmpty();
        AesCbc.DecryptCbc(NistKey, NistIv, []).Should().BeEmpty();
    }

    [Fact]
    public void AKeyThatIsNot256BitIsRefused()
        => FluentActions.Invoking(() => AesCbc.EncryptCbc(NistKey[..16], NistIv, NistPlain))
            .Should().Throw<AesException>().WithMessage("*32 bytes*");

    [Fact]
    public void AnIvThatIsNot16BytesIsRefused()
        => FluentActions.Invoking(() => AesCbc.DecryptCbc(NistKey, NistIv[..8], NistCipher))
            .Should().Throw<AesException>().WithMessage("*16 bytes*");

    [Fact]
    public void InputThatIsNotWholeBlocksIsRefused()
        => FluentActions.Invoking(() => AesCbc.DecryptCbc(NistKey, NistIv, NistCipher[..^1]))
            .Should().Throw<AesException>().WithMessage("*multiple of 16*");

    [Fact]
    public void EncryptingAddsNoPaddingBlock() => AesCbc.EncryptCbc(NistKey, NistIv, NistPlain).Length.Should().Be(NistPlain.Length);

    [Fact]
    public void EcbDecryptMatchesTheNistAes128Vector() => AesCbc.DecryptEcb(Ecb128Key, Ecb128Cipher).Should().Equal(Ecb128Plain);

    [Fact]
    public void EcbDecryptMatchesTheFips197Block() => AesCbc.DecryptEcb(FipsKey, FipsCipher).Should().Equal(FipsPlain);

    [Fact]
    public void EcbHasNoChainingSoEqualBlocksDecryptAlike()
    {
        var out2 = AesCbc.DecryptEcb(Ecb128Key, [..Ecb128Cipher[..16], ..Ecb128Cipher[..16]]);
        out2[..16].Should().Equal(out2[16..]);
    }

    [Fact]
    public void EcbRefusesAKeyThatIsNot128Bit()
        => FluentActions.Invoking(() => AesCbc.DecryptEcb(new byte[24], Ecb128Cipher)).Should().Throw<AesException>();

    [Fact]
    public void EcbRefusesInputThatIsNotWholeBlocks()
        => FluentActions.Invoking(() => AesCbc.DecryptEcb(Ecb128Key, Ecb128Cipher[..^1])).Should().Throw<AesException>();

    [Fact]
    public void EcbEmptyInputGivesEmptyOutput() => AesCbc.DecryptEcb(Ecb128Key, []).Should().BeEmpty();
}
