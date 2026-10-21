using System.Text.Json;
using FluentAssertions;
using NexusMods.Games.Declarative.Catalog;

namespace NexusMods.Games.Declarative.Tests;

/// <summary>
/// A definition carries its Nexus domain and id literally, because the lookup that resolves them lives in
/// the container and queries the network on a miss. These tests check the literals against the list
/// of games shipped with the app.
/// </summary>
public class NexusGameIdTests(GameDefinitionCatalog catalog)
{
    private static ILookup<string, long> LoadVendoredGames()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "games.json"));
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.EnumerateArray().ToLookup(game => game.GetProperty("domain_name").GetString()!, game => game.GetProperty("id").GetInt64());
    }

    [Fact]
    public void TheVendoredListKnowsTheBethesdaDomainsDefinitionsUse()
    {
        var games = LoadVendoredGames();

        games["oblivion"].Should().Equal(101);
        games["skyrimspecialedition"].Should().Equal(1704);
        games["fallout4"].Should().Equal(1151);
    }

    [Fact]
    public void EveryBundledNexusDomainAndIdMatchesTheVendoredList()
    {
        var games = LoadVendoredGames();

        // Only what ships, embedded under games/; test resources are named test/ and may use ids Nexus doesn't list.
        var wrong = catalog.Definitions
            .Where(loaded => loaded.Source.StartsWith("games/", StringComparison.Ordinal))
            .Where(loaded => loaded.Definition.Nexus is not null)
            .Where(loaded => !games[loaded.Definition.Nexus!.Domain].Contains(loaded.Definition.Nexus.Id))
            .Select(loaded => $"{loaded.Source}: {loaded.Definition.Nexus!.Domain} {loaded.Definition.Nexus.Id}");

        wrong.Should().BeEmpty();
    }
}
