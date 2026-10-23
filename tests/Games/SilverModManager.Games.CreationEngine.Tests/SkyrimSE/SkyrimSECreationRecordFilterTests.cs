using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.SkyrimSE;

public class SkyrimSECreationRecordFilterTests(IServiceProvider serviceProvider)
{
    private readonly IGamePathFilter _filter = serviceProvider
        .GetServices<IGame>().OfType<CreationEngine.SkyrimSE.SkyrimSE>().Single().Synchronizer.GamePathFilter;

    [Theory]
    [InlineData("AppData", "ContentCatalog.txt", true)]
    [InlineData("Game", "Creations", true)]
    [InlineData("Game", "Creations/ccinvented001-1700000000.manifest", true)]
    [InlineData("Game", "Data/ccinvented001-garden.esm", false)]
    [InlineData("Game", "Data/ccinvented001-garden.bsa", false)]
    [InlineData("AppData", "Plugins.txt", false)]
    [InlineData("Game", "Creations.txt", false)]
    public void CreationRecordsAreFilteredAndTheirContentIsNot(string location, string path, bool expected)
    {
        _filter.ShouldFilter(new GamePath(LocationId.From(location), path)).Should().Be(expected);
    }
}
