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
    public void ShouldFilter(string location, string path, bool expected)
    {
        _filter.ShouldFilter(new GamePath(LocationId.From(location), path)).Should().Be(expected);
    }
}
