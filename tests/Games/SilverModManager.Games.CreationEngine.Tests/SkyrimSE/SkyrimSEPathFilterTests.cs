using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.SkyrimSE;

public class SkyrimSEPathFilterTests(IServiceProvider serviceProvider)
{
    private readonly IGamePathFilter _filter = serviceProvider
        .GetServices<IGame>().OfType<CreationEngine.SkyrimSE.SkyrimSE>().Single().Synchronizer.GamePathFilter;

    [Theory]
    [InlineData("Preferences", "Saves/x.ess", true)]
    [InlineData("Saves", "x", true)]
    [InlineData("Preferences", "Skyrim.ini", false)]
    [InlineData("Preferences", "SKSE/crash-2026-11-09-15-30-26.log", true)]
    [InlineData("Preferences", "SKSE/MergeMapper.log", true)]
    [InlineData("Preferences", "SKSE/OCUGestureSpellList.json", true)]
    [InlineData("Preferences", "SKSE/Crashdumps/x.dmp", true)]
    public void ShouldFilter(string location, string path, bool expected)
    {
        _filter.ShouldFilter(new GamePath(LocationId.From(location), path)).Should().Be(expected);
    }
}
