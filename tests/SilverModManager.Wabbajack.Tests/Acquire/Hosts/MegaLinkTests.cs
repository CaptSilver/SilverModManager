using FluentAssertions;
using SilverModManager.Wabbajack.Acquire.Hosts;

namespace SilverModManager.Wabbajack.Tests.Acquire.Hosts;

public class MegaLinkTests
{
    /// <summary>Thirty-two bytes whose base64url form is 43 characters.</summary>
    internal static readonly byte[] FileKey = Enumerable.Range(1, 32).Select(i => (byte)(i * 9)).ToArray();

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Theory]
    [InlineData("https://mega.nz/file/AbCdEf12#{0}")]
    [InlineData("https://mega.nz/#!AbCdEf12!{0}")]
    [InlineData("https://mega.co.nz/#!AbCdEf12!{0}")]
    [InlineData("https://www.mega.nz/file/AbCdEf12#{0}")]
    public void BothLinkFormsGiveTheFileIdAndKey(string template)
    {
        var url = new Uri(string.Format(template, Base64Url(FileKey)));

        MegaLink.TryParse(url, out var link).Should().BeTrue();

        link!.FileId.Should().Be("AbCdEf12");
        link.Key.Should().Equal(FileKey);
    }

    [Fact]
    public void TheAesKeyIsTheFirstHalfOfTheKeyXoredWithTheSecond()
    {
        var key = new byte[32];
        for (var i = 0; i < 16; i++) key[i] = (byte)(0xF0 | i);
        for (var i = 16; i < 32; i++) key[i] = 0xFF;

        MegaLink.TryParse(new Uri($"https://mega.nz/file/x#{Base64Url(key)}"), out var link).Should().BeTrue();

        link!.AesKey.Should().Equal(Enumerable.Range(0, 16).Select(i => (byte)((0xF0 | i) ^ 0xFF)));
    }

    [Fact]
    public void TheNonceIsTheFirstEightBytesOfTheSecondHalf()
    {
        MegaLink.TryParse(new Uri($"https://mega.nz/file/x#{Base64Url(FileKey)}"), out var link).Should().BeTrue();

        link!.Nonce.Should().Equal(FileKey.Skip(16).Take(8));
    }

    [Theory]
    [InlineData("https://mega.nz/folder/AbCdEf12#abc")]
    [InlineData("https://mega.nz/file/AbCdEf12")]
    [InlineData("https://mega.nz/file/AbCdEf12#tooShort")]
    [InlineData("https://mega.nz/file/#AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("https://example.com/file/AbCdEf12#AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("https://mega.nz/#F!AbCdEf12!AAAAAAAAAAAAAAAAAAAAAA")]
    public void ALinkThatIsNotAFileLinkIsRejected(string url)
    {
        MegaLink.TryParse(new Uri(url), out var link).Should().BeFalse();
        link.Should().BeNull();
    }
}
