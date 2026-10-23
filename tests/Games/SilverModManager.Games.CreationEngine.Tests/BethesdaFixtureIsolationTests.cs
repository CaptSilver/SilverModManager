using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests;

/// <summary>
/// Bethesda fixtures that sync a loadout must not point the game's Documents and AppData locations at the
/// machine's real home or XDG folders: Plugins.txt and the ini files there would be shared by every test
/// running in parallel (and by the developer's own game), so one test writing them changes the bytes another
/// test is backing up.
/// </summary>
public class BethesdaFixtureIsolationTests(ITestOutputHelper helper)
    : ABethesdaInstallerRoutingTest<BethesdaFixtureIsolationTests, CreationEngine.SkyrimSE.SkyrimSE>(helper)
{
    protected override Version GameVersion => new("1.6.1170");

    [Fact]
    public void ThePluginsFileResolvesInsideATemporaryPrefix() =>
        GameInstallation.Locations.ToAbsolutePath(Game.PluginsFile).GetFullPath()
            .Should().Contain("/drive_c/users/", "Plugins.txt must not resolve to the real AppData folder");

    [Fact]
    public void ThePreferencesFolderResolvesInsideATemporaryPrefix() =>
        GameInstallation.Locations[LocationId.Preferences].Path
            .GetFullPath().Should().Contain("/drive_c/users/", "the ini files must not resolve to the real Documents folder");
}
