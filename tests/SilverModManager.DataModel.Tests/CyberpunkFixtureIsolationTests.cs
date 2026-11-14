using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// The Cyberpunk fixture's AppData location is part of every loadout's first sync. If it resolved to the real XDG
/// data folder, every fixture in a run (and every test project running beside this one) would share it, and a file
/// one test writes there would show up as an Overrides row in another test's loadout.
/// </summary>
public class CyberpunkFixtureIsolationTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<CyberpunkFixtureIsolationTests>(helper)
{
    [Fact]
    public async Task TheAppDataLocationIsNotInTheSharedXdgDataFolder()
    {
        var loadout = await CreateLoadout();

        var appData = loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(LocationId.AppData, ""));
        var sharedDataFolder = NexusMods.Paths.FileSystem.Shared.GetKnownPath(KnownPath.LocalApplicationDataDirectory);

        appData.InFolder(sharedDataFolder).Should().BeFalse(
            "a folder every fixture of the run shares lets one test's files land in another test's Overrides group");
    }
}
