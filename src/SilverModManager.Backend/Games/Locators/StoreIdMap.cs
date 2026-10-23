using System.Collections.Frozen;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Backend.Games.Locators;

/// <summary>
/// The games a locator can match, by the id their store knows them under. Two games that claim one id (a user
/// definition reusing a bundled game's id, say) must not take the locator down, so the first one registered
/// keeps the id and the clash is logged and kept as a problem. Coded and bundled games register before user
/// definitions, which is what makes first-wins the right rule.
/// </summary>
internal sealed record StoreIdMap<TKey>(FrozenDictionary<TKey, IGameData> Games, ImmutableArray<GameLocatorProblem> Problems) where TKey : notnull
{
    public void ReportTo(IGameLocatorProblemSink sink)
    {
        foreach (var problem in Problems) sink.Report(problem.Locator, problem.Message);
    }
}

internal static class StoreIdMap
{
    public static StoreIdMap<TKey> Build<TKey>(IEnumerable<IGameData> games, Func<IGameData, IEnumerable<TKey>> idsOf, string store, string locator, ILogger logger)
        where TKey : notnull
    {
        var map = new Dictionary<TKey, IGameData>();
        var problems = ImmutableArray.CreateBuilder<GameLocatorProblem>();

        foreach (var game in games)
        {
            foreach (var id in idsOf(game))
            {
                if (!map.TryGetValue(id, out var existing))
                {
                    map.Add(id, game);
                    continue;
                }

                // A game listing its own id twice is harmless.
                if (ReferenceEquals(existing, game)) continue;

                var message = $"{store} id {id} is claimed by both '{existing.DisplayName}' and '{game.DisplayName}'; '{existing.DisplayName}' keeps it";
                logger.LogError("{Locator}: {Message}", locator, message);
                problems.Add(new GameLocatorProblem(locator, message));
            }
        }

        return new StoreIdMap<TKey>(map.ToFrozenDictionary(), problems.ToImmutable());
    }
}
