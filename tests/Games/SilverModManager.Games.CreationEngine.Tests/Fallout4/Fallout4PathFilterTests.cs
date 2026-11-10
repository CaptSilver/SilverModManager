using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.Fallout4;

public class Fallout4PathFilterTests(IServiceProvider serviceProvider)
{
    private readonly IGamePathFilter _filter = serviceProvider
        .GetServices<IGame>().OfType<CreationEngine.Fallout4.Fallout4>().Single().Synchronizer.GamePathFilter;

    // Only Skyrim's script extender folder is the game's: Fallout 4 has none declared, so its logs stay ordinary files.
    [Theory]
    [InlineData("Preferences", "F4SE/f4se.log", false)]
    [InlineData("Preferences", "F4SE/crash-x.log", false)]
    [InlineData("Preferences", "Saves/x.fos", true)]
    public void ShouldFilter(string location, string path, bool expected)
    {
        _filter.ShouldFilter(new GamePath(LocationId.From(location), path)).Should().Be(expected);
    }
}
