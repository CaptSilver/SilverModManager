using FluentAssertions;
using SilverModManager.Networking.Thunderstore;

namespace SilverModManager.UI.Tests.Pages;

public class ThunderstoreIconUrlTests
{
    [Fact]
    public void TheIconUrl_IsTheCdnPathOfTheVersion()
    {
        ThunderstoreIcons.UrlFor("BepInEx", "BepInExPack", "5.4.2305")
            .Should().Be(new Uri("https://gcdn.thunderstore.io/live/repository/icons/BepInEx-BepInExPack-5.4.2305.png"));
    }

    [Fact]
    public void OddCharacters_AreEscapedIntoOnePathSegment()
    {
        var url = ThunderstoreIcons.UrlFor("Some Team", "Mod?Name", "1.0.0");

        url.AbsolutePath.Should().Be("/live/repository/icons/Some%20Team-Mod%3FName-1.0.0.png");
        url.Query.Should().BeEmpty();
    }
}
