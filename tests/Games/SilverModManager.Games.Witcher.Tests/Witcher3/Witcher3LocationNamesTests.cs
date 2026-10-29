using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Witcher.Tests.Witcher3;

public class Witcher3LocationNamesTests(ITestOutputHelper helper)
    : AIsolatedGameTest<Witcher3LocationNamesTests, Witcher3Game>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddWitcherGames()
        .AddUniversalGameLocator<Witcher3Game>(new Version("4.0"), locationsFileSystem: IsolatedKnownFolders.Create);

    [Fact]
    public void TheGameNamesItsDocumentsLocationForAnIdParsedFromText()
    {
        var id = LocationId.From("WitcherDocuments");

        ((IGameData)Game).LocationNames[id.ToString()].Should().Be("Documents");
    }
}
