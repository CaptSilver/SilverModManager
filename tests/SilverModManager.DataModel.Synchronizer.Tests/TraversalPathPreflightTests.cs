using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Backend;
using SilverModManager.CrossPlatform;
using SilverModManager.Games.Generic;
using SilverModManager.Games.RedEngine;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A loadout file whose path climbs out of its location, is rooted or names a drive must stop the sync before
/// anything is written, whichever game the loadout is for.
/// </summary>
public class TraversalPathPreflightTests(ITestOutputHelper helper) : ASteamInstalledGameTest<TraversalPathPreflightTests, Cyberpunk2077Game>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddOSInterop()
            .AddRuntimeDependencies()
            .RemoveRuntimeDependencyChecker()
            .AddGenericGameSupport()
            .AddFakeToolRunner()
            .AddRedEngineGames();
    }

    protected override SteamInstall InstallGame(SteamLibraryFixture library) => CyberpunkSteamInstall.Install(library);

    // The Paths library neither resolves dot segments nor rejects a ':' inside a path, so the preflight has to.
    // Rooted input never reaches the synchronizer: RelativePath refuses it on construction.
    [Theory]
    [InlineData("r6/../x.reds")]
    [InlineData("./x.reds")]
    [InlineData("r6/./x.reds")]
    [InlineData("../x.reds")]
    [InlineData("r6/C:/x")]
    public void RelativePathKeepsDotSegmentsAndColonsAsTheyAre(string input) =>
        ((RelativePath)input).Path.Should().Be(input);

    [Theory]
    [InlineData("/etc/x")]
    [InlineData("C:/x")]
    public void RelativePathRefusesRootedInput(string input)
    {
        var act = () => (RelativePath)input;

        act.Should().Throw<NexusMods.Paths.Utilities.PathException>();
    }

    [Theory]
    [InlineData("r6/../../outside.reds")]
    [InlineData("../outside.reds")]
    [InlineData("r6/./mod.reds")]
    [InlineData("r6/C:/mod.reds")]
    public async Task ALoadoutFileWithATraversalPathStopsTheSyncBeforeAnythingIsWritten(string path)
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, [(RelativePath)"r6/scripts/fine.reds", (RelativePath)path], loadout.LoadoutId, "Evil mod");
            await tx.Commit();
        }

        var act = async () => await Synchronizer.Synchronize(Refresh(loadout));

        var exception = (await act.Should().ThrowAsync<SynchronizerPreflightException>()).Which;
        exception.Message.Should().Contain("Evil mod");
        (GameFolder / "r6/scripts/fine.reds").FileExists.Should().BeFalse();
    }
}
