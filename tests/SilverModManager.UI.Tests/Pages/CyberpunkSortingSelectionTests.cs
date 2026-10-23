using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.RedEngine.Cyberpunk2077.SortOrder;
using NexusMods.Sdk.Games;

namespace NexusMods.UI.Tests.Pages;

public class CyberpunkSortingSelectionTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public void CyberpunkListsItsVarietyWhateverTheOtherGamesRegister()
    {
        // The Load Order page reads game.SortOrderManager; resolve every game's manager so each has registered.
        var games = Provider.GetServices<IGame>().ToArray();
        foreach (var game in games) _ = game.SortOrderManager;

        var cyberpunk = games.OfType<Cyberpunk2077Game>().Single();
        cyberpunk.SortOrderManager.GetSortOrderVarieties().Should().ContainSingle(v => v is RedModSortOrderVariety);
    }
}
