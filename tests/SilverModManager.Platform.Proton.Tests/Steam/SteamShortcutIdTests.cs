using System.IO.Hashing;
using System.Text;
using FluentAssertions;
using SilverModManager.Platform.Proton.Steam.Shortcuts;

namespace SilverModManager.Platform.Proton.Tests.Steam;

public class SteamShortcutIdTests
{
    [Fact]
    public void AppIdIsCrc32OfExeAndNameWithTheHighBitSet()
    {
        const string exe = "\"/x/silvermod\"", name = "SilverModManager";
        var expected = Crc32.HashToUInt32(Encoding.UTF8.GetBytes(exe + name)) | 0x8000_0000;

        var appId = SteamShortcutId.AppId(exe, name);

        appId.Should().Be(expected);
        (appId & 0x8000_0000).Should().NotBe(0);
        SteamShortcutId.LaunchId(appId).Should().Be(((ulong)appId << 32) | 0x0200_0000);
        (SteamShortcutId.LaunchId(appId) >> 32).Should().Be(appId);
        (SteamShortcutId.LaunchId(appId) & 0xFFFF_FFFF).Should().Be(0x0200_0000);
    }

    [Fact]
    public void AKnownKeyHasAKnownId()
    {
        // CRC-32/IEEE of "123456789" is 0xCBF43926.
        SteamShortcutId.AppId("12345", "6789").Should().Be(0xCBF43926u | 0x8000_0000);
    }
}
