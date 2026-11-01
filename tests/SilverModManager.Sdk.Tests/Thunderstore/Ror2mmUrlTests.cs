using FluentAssertions;
using SilverModManager.Sdk.Thunderstore;

namespace SilverModManager.Sdk.Tests.Thunderstore;

public class Ror2mmUrlTests
{
    private static readonly ThunderstoreVersionRef RepoLib = new(new ThunderstorePackageRef("Zehs", "REPOLib"), new ThunderstoreVersionNumber(4, 3, 0));

    [Theory]
    [InlineData("ror2mm://v1/install/thunderstore.io/Zehs/REPOLib/4.3.0/")]
    [InlineData("ror2mm://v1/install/thunderstore.io/Zehs/REPOLib/4.3.0")]
    [InlineData("ror2mm://v1/install/riskofrain2.thunderstore.io/Zehs/REPOLib/4.3.0/")]
    [InlineData("ROR2MM://v1/install/thunderstore.io/Zehs/REPOLib/4.3.0/")]
    public void TryParse_WithInstallLink_ReadsPackageAndVersion(string link)
    {
        Ror2mmUrl.TryParse(new Uri(link), out var url, out var problem).Should().BeTrue(problem);

        url.Version.Should().Be(RepoLib);
    }

    [Fact]
    public void TryParse_WithHyphenatedTeam_KeepsTheNamespace()
    {
        Ror2mmUrl.TryParse(new Uri("ror2mm://v1/install/thunderstore.io/Legacy-Team/Mod/1.0.0/"), out var url, out _).Should().BeTrue();

        url.Version.Package.Should().Be(new ThunderstorePackageRef("Legacy-Team", "Mod"));
    }

    [Theory]
    [InlineData("ror2mm://v2/install/thunderstore.io/Zehs/REPOLib/4.3.0/")]
    [InlineData("ror2mm://v1/uninstall/thunderstore.io/Zehs/REPOLib/4.3.0/")]
    [InlineData("ror2mm://v1/install/example.com/Zehs/REPOLib/4.3.0/")]
    [InlineData("ror2mm://v1/install/evilthunderstore.io/Zehs/REPOLib/4.3.0/")]
    [InlineData("ror2mm://v1/install/a.b.thunderstore.io/Zehs/REPOLib/4.3.0/")]
    [InlineData("ror2mm://v1/install/thunderstore.io/Zehs/REPOLib/4.3/")]
    [InlineData("ror2mm://v1/install/thunderstore.io/Zehs/REPOLib/latest/")]
    [InlineData("ror2mm://v1/install/thunderstore.io/Zehs/RE-POLib/4.3.0/")]
    [InlineData("ror2mm://v1/install/thunderstore.io/Zehs/REPOLib/")]
    [InlineData("ror2mm://v1/install/thunderstore.io/Zehs/REPOLib/4.3.0/extra/")]
    [InlineData("nxm://v1/install/thunderstore.io/Zehs/REPOLib/4.3.0/")]
    public void TryParse_WithUnsupportedLink_FailsQuotingTheLink(string link)
    {
        Ror2mmUrl.TryParse(new Uri(link), out var url, out var problem).Should().BeFalse();

        url.Should().Be(default(Ror2mmUrl));
        problem.Should().Contain(link);
        problem.Should().Contain("ror2mm://v1/install/thunderstore.io/");
    }
}
