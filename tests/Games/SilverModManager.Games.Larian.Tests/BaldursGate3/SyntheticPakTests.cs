using System.Text;
using FluentAssertions;

namespace SilverModManager.Games.Larian.Tests.BaldursGate3;

public class SyntheticPakTests
{
    [Theory]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(13)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(18)]
    public void EveryVersionStartsOrEndsTheWayLsLibExpects(int version)
    {
        var pak = SyntheticPak.Build(version, ("Mods/X/meta.lsx", "<save/>"));

        switch (version)
        {
            case 7 or 9:
                BitConverter.ToInt32(pak, 0).Should().Be(version);
                break;
            case 13:
                Encoding.ASCII.GetString(pak, pak.Length - 4, 4).Should().Be("LSPK");
                BitConverter.ToInt32(pak, pak.Length - 8).Should().BeGreaterThan(0);
                break;
            default:
                Encoding.ASCII.GetString(pak, 0, 4).Should().Be("LSPK");
                BitConverter.ToInt32(pak, 4).Should().Be(version);
                break;
        }
    }
}
