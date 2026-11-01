using FluentAssertions;
using SilverModManager.Wabbajack.Acquire.Hosts;

namespace SilverModManager.Wabbajack.Tests.Acquire.Hosts;

/// <summary>Which host's job takes an address a list gives, judged from the address alone.</summary>
public class HostDownloadersTests
{
    [Theory]
    [InlineData("https://www.mediafire.com/file/abc123xyz/Some_Mod.7z/file", "MediaFire")]
    [InlineData("https://www.mediafire.com/file/abc123xyz", "MediaFire")]
    [InlineData("https://mediafire.com/file/abc123xyz/Some_Mod.7z", "MediaFire")]
    [InlineData("https://download1234.mediafire.com/abcd/xyz/Some_Mod.7z", "MediaFire")]
    [InlineData("https://www.moddb.com/downloads/start/123456", "ModDB")]
    [InlineData("https://www.moddb.com/downloads/mirror/123456/118/0123abcd/", "ModDB")]
    [InlineData("https://www.moddb.com/mods/cool-mod/addons/cool-patch", "ModDB")]
    [InlineData("https://www.moddb.com/mods/cool-mod/downloads/cool-file", "ModDB")]
    [InlineData("https://www.moddb.com/addons/cool-patch", "ModDB")]
    [InlineData("https://www.moddb.com/downloads/start/123456?referer=x", "ModDB")]
    [InlineData("https://mega.nz/file/AbCdEf12#AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "MEGA")]
    [InlineData("https://mega.nz/#!AbCdEf12!AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "MEGA")]
    [InlineData("https://drive.google.com/file/d/1AbCdEfGhIjKlMnOp/view?usp=sharing", "Google Drive")]
    [InlineData("https://drive.google.com/open?id=1AbCdEfGhIjKlMnOp", "Google Drive")]
    [InlineData("https://drive.google.com/uc?id=1AbCdEfGhIjKlMnOp&export=download", "Google Drive")]
    [InlineData("https://drive.usercontent.google.com/download?id=1AbCdEfGhIjKlMnOp&export=download", "Google Drive")]
    public void AnAddressAHostCanFetchGoesToThatHost(string address, string host)
    {
        var found = HostDownloaders.For(new Uri(address));

        found.Should().NotBeNull();
        found.Name.Should().Be(host);
    }

    [Theory]
    [InlineData("https://www.mediafire.com/folder/abc123xyz/Mods")]
    [InlineData("https://www.mediafire.com/")]
    [InlineData("http://www.mediafire.com/file/abc123xyz/Some_Mod.7z/file")]
    [InlineData("https://notmediafire.com/file/abc123xyz/Some_Mod.7z")]
    [InlineData("https://www.moddb.com/mods/cool-mod")]
    [InlineData("https://www.moddb.com/mods/cool-mod/downloads")]
    [InlineData("https://www.moddb.com/members/someone")]
    [InlineData("http://www.moddb.com/downloads/start/123456")]
    [InlineData("https://evilmoddb.com/downloads/start/123456")]
    [InlineData("https://mega.nz/folder/AbCdEf12#AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("https://mega.nz/file/AbCdEf12")]
    [InlineData("https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOp")]
    [InlineData("https://drive.google.com/file/d/")]
    [InlineData("https://docs.google.com/document/d/1AbCdEfGhIjKlMnOp/edit")]
    [InlineData("https://drive.google.com.example.test/file/d/1AbCdEfGhIjKlMnOp/view")]
    [InlineData("https://example.invalid/file")]
    public void AnAddressNoHostCanFetchGoesNowhere(string address)
    {
        HostDownloaders.For(new Uri(address)).Should().BeNull();
    }

    [Fact]
    public void EveryHostHasItsOwnName()
    {
        HostDownloaders.All.Select(h => h.Name).Should().OnlyHaveUniqueItems().And.BeEquivalentTo("MediaFire", "ModDB", "MEGA", "Google Drive");
    }
}
