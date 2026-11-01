using System.Collections.Frozen;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Backend.Games.Locators;

/// <summary>
/// The games a locator can match, by the id their store knows them under. An id can have several claimants: a game
/// installed over another game's files (Fallout 4 London on Fallout 4's Steam id) names the files only its install
/// has through <see cref="IGameIdentifyingFiles"/>, and <see cref="Resolve"/> tells the claimants apart by the
/// install folder.
/// </summary>
/// <remarks>
/// Claimants that no folder could ever tell apart (the same identifying files, or none) are a clash: the first one
/// registered keeps the id, and the clash is logged and kept as a problem. Coded and bundled games register before
/// user definitions, which is what makes first-wins the right rule.
/// </remarks>
internal sealed record StoreIdMap<TKey>(
    FrozenDictionary<TKey, ImmutableArray<IGameData>> Claimants,
    ImmutableArray<GameLocatorProblem> Problems,
    string Store,
    string Locator,
    ILogger Logger) where TKey : notnull
{
    public void ReportTo(IGameLocatorProblemSink sink)
    {
        foreach (var problem in Problems) sink.Report(problem.Locator, problem.Message);
    }

    public bool Contains(TKey id) => Claimants.ContainsKey(id);

    /// <summary>Whether <paramref name="game"/> is one of the games that claim the id.</summary>
    public bool Claims(TKey id, IGameData game)
        => Claimants.TryGetValue(id, out var claimants) && claimants.Any(claimant => claimant.GameId == game.GameId);

    /// <summary>
    /// The game for an id when the install folder is not known: the claimant that names no identifying files, which
    /// is the one every folder is until a file says otherwise, or the first claimant when all of them name files.
    /// </summary>
    public IGameData? Default(TKey id)
    {
        if (!Claimants.TryGetValue(id, out var claimants)) return null;
        return claimants.FirstOrDefault(claimant => IdentifyingFilesNarrowing.DeclaredBy(claimant).Count == 0) ?? claimants[0];
    }

    /// <summary>
    /// The claimant whose identifying files the folder holds, or the only claimant. Null when no game claims the id
    /// or when every claimant names files the folder lacks. When files cannot separate the claimants (more than one
    /// has all of its files) the first of them keeps the id and the clash goes to <paramref name="problems"/>.
    /// </summary>
    public IGameData? Resolve(TKey id, AbsolutePath folder, IGameLocatorProblemSink? problems)
    {
        if (!Claimants.TryGetValue(id, out var claimants)) return null;
        if (claimants.Length == 1) return claimants[0];

        var remaining = IdentifyingFilesNarrowing.Narrow(folder, claimants.ToList(), game => game);
        if (remaining.Count == 1) return remaining[0];
        if (remaining.Count == 0) return null;

        var message = $"{Store} id {id} is claimed by " + string.Join(" and ", remaining.Select(game => $"'{game.DisplayName}'")) +
                      $", and the files in {folder} do not tell them apart; '{remaining[0].DisplayName}' keeps it";
        Logger.LogError("{Locator}: {Message}", Locator, message);
        problems?.Report(Locator, message);
        return remaining[0];
    }
}

internal static class StoreIdMap
{
    public static StoreIdMap<TKey> Build<TKey>(IEnumerable<IGameData> games, Func<IGameData, IEnumerable<TKey>> idsOf, string store, string locator, ILogger logger)
        where TKey : notnull
    {
        var map = new Dictionary<TKey, List<IGameData>>();
        var problems = ImmutableArray.CreateBuilder<GameLocatorProblem>();

        foreach (var game in games)
        {
            foreach (var id in idsOf(game))
            {
                if (!map.TryGetValue(id, out var claimants))
                {
                    map.Add(id, [game]);
                    continue;
                }

                // A game listing its own id twice is harmless.
                if (claimants.Any(claimant => ReferenceEquals(claimant, game))) continue;

                var twin = claimants.FirstOrDefault(claimant => NamesTheSameFiles(claimant, game));
                if (twin is null)
                {
                    claimants.Add(game);
                    continue;
                }

                var message = $"{store} id {id} is claimed by both '{twin.DisplayName}' and '{game.DisplayName}'; '{twin.DisplayName}' keeps it";
                logger.LogError("{Locator}: {Message}", locator, message);
                problems.Add(new GameLocatorProblem(locator, message));
            }
        }

        return new StoreIdMap<TKey>(
            map.ToFrozenDictionary(entry => entry.Key, entry => entry.Value.ToImmutableArray()),
            problems.ToImmutable(), store, locator, logger);
    }

    // The same set of files means no folder has one game's and not the other's.
    private static bool NamesTheSameFiles(IGameData a, IGameData b) => FileKeys(a).SequenceEqual(FileKeys(b));

    private static IEnumerable<string> FileKeys(IGameData game)
        => IdentifyingFilesNarrowing.DeclaredBy(game).Select(file => file.ToString().ToLowerInvariant()).Order();
}
