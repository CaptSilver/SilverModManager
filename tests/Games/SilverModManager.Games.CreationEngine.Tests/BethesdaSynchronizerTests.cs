using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests;

public class BethesdaSynchronizerTests(IServiceProvider serviceProvider)
{
    private readonly ILoadoutSynchronizer _synchronizer = serviceProvider
        .GetServices<IGame>().OfType<CreationEngine.SkyrimVR.SkyrimVR>().Single().Synchronizer;

    [Theory]
    [InlineData("SKSE/sksevr.log")]
    [InlineData("SKSE/MergeMapper.log")]
    [InlineData("SKSE/OCUGestureSpellList.json")]
    [InlineData("SKSE/SVRPhysicalContainers-crash.txt")]
    [InlineData("SKSE/Crashdumps/x.dmp")]
    public void EverythingUnderTheScriptExtenderLogFolderIsGameWritten(string path)
    {
        _synchronizer.IsGameWrittenPath(new GamePath(LocationId.Preferences, path)).Should().BeTrue();
    }

    [Theory]
    [InlineData("Preferences", "SkyrimVR.ini", false)]
    [InlineData("Game", "Data/SKSE/Plugins/x.dll", false)]
    [InlineData("Game", "Data/SKSE/Plugins/x.log", true)]
    public void AFileOutsideTheFolderFollowsTheDefaultRule(string location, string path, bool expected)
    {
        _synchronizer.IsGameWrittenPath(new GamePath(LocationId.From(location), path)).Should().Be(expected);
    }
}
