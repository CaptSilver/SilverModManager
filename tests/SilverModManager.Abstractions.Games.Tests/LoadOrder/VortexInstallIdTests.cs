using FluentAssertions;
using SilverModManager.Abstractions.Games.LoadOrder;

namespace SilverModManager.Abstractions.Games.Tests.LoadOrder;

public class VortexInstallIdTests
{
    [Theory]
    [InlineData("Smooth Map - Map Remastered Version 13530 1.0 2026-10-12T17-56Z 864tMlsGA", "Smooth Map - Map Remastered Version", 13530u, "1.0")]
    [InlineData("Actual Items In Bestiary 13378 1 2026-10-03T16-30Z yShfiQago", "Actual Items In Bestiary", 13378u, "1")]
    [InlineData("Extended Death Screen 5.00.1 (Remastered) 8060 5.00.1 2026-10-05T09-12Z Qw3rTyUiO", "Extended Death Screen 5.00.1 (Remastered)", 8060u, "5.00.1")]
    [InlineData("BASE - All 12954 5.01.3 2026-10-07T11-00Z aBcDeFgHi", "BASE - All", 12954u, "5.01.3")]
    public void TryParse_Spaced(string id, string name, uint modId, string version)
    {
        VortexInstallId.TryParse(id, out var parsed).Should().BeTrue();
        parsed.Should().Be(new VortexInstallId(name, modId, version));
    }

    [Theory]
    [InlineData("Better Icons-2000-1-0-1700000000", "Better Icons", 2000u)]
    [InlineData("Friendly HUD-365-1234-3-2-1700000000", "Friendly HUD", 365u)]
    [InlineData("Name-123-1700000000", "Name", 123u)]
    public void TryParse_DashedArchiveName(string id, string name, uint modId)
    {
        VortexInstallId.TryParse(id, out var parsed).Should().BeTrue();
        parsed.Should().Be(new VortexInstallId(name, modId, null));
    }

    [Fact]
    public void TryParse_DashedNameWithNumbers()
    {
        VortexInstallId.TryParse("Witcher-3-HD-1234-1-0-1700000000", out var parsed).Should().BeTrue();
        parsed.NexusModId.Should().Be(1234u);
    }

    [Theory]
    [InlineData("Friendly HUD")]
    [InlineData("Mod Menu v2.1")]
    [InlineData("")]
    public void TryParse_RejectsAPlainName(string id) => VortexInstallId.TryParse(id, out _).Should().BeFalse();
}
