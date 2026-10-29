using FluentAssertions;
using SilverModManager.Games.TestFramework.Helpers;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

public class SteamLibraryFixtureTests
{
    [Fact]
    public void DisposingRemovesTheWholeFolder()
    {
        var fixture = new SteamLibraryFixture();
        var root = fixture.Root.ToString();
        fixture.AddGame(1, "Game", "Game", 1, new SteamDepot(2, 3, [new SteamFile("bin/game.exe", "exe")]));
        Directory.Exists(root).Should().BeTrue();

        fixture.Dispose();

        Directory.Exists(root).Should().BeFalse();
    }

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        var fixture = new SteamLibraryFixture();

        fixture.Dispose();
        var again = fixture.Dispose;

        again.Should().NotThrow();
    }

    [Fact]
    public void DisposingRemovesFoldersAGameLeftReadOnly()
    {
        var fixture = new SteamLibraryFixture();
        var root = fixture.Root.ToString();
        var install = fixture.AddGame(1, "Game", "Game", 1, new SteamDepot(2, 3, [new SteamFile("locked/game.dll", "dll")]));
        var locked = Path.Combine(install.Folder.ToString(), "locked");
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        fixture.Dispose();

        Directory.Exists(root).Should().BeFalse();
    }
}
