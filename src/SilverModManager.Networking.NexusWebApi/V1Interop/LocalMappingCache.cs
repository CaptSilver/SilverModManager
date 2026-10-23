using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Networking.NexusWebApi.V1Interop;

internal class LocalMappingCache : IGameDomainToGameIdMappingCache
{
    private readonly ILogger _logger;
    private readonly FrozenDictionary<NexusModsGameId, GameDomain> _gameIdToDomain;
    private readonly FrozenDictionary<GameDomain, NexusModsGameId> _gameDomainToId;
    private readonly IGameDomainToGameIdMappingCache _fallbackCache;

    public LocalMappingCache(
        ILogger<LocalMappingCache> logger,
        FrozenDictionary<NexusModsGameId, GameDomain> gameIdToDomain,
        FrozenDictionary<GameDomain, NexusModsGameId> gameDomainToId,
        IGameDomainToGameIdMappingCache fallbackCache)
    {
        _logger = logger;
        _gameIdToDomain = gameIdToDomain;
        _gameDomainToId = gameDomainToId;
        _fallbackCache = fallbackCache;
    }

    public GameDomain GetDomain(NexusModsGameId id)
    {
        if (_gameIdToDomain.TryGetValue(id, out var domain)) return domain;
        _logger.LogDebug("Using fallback cache for game id {Id}", id);

        return _fallbackCache[id];
    }

    public NexusModsGameId GetId(GameDomain domain)
    {
        if (_gameDomainToId.TryGetValue(domain, out var id)) return id;
        _logger.LogDebug("Using fallback cache for game id {Domain}", domain);

        return _fallbackCache[domain];
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        TypeInfoResolver = GameMetadataContext.Default,
    };

    internal static bool TryParseJsonFile(
        ILogger logger,
        [NotNullWhen(true)] out FrozenDictionary<NexusModsGameId, GameDomain>? gameIdToDomain,
        [NotNullWhen(true)] out FrozenDictionary<GameDomain, NexusModsGameId>? gameDomainToId)
    {
        gameIdToDomain = null;
        gameDomainToId = null;

        try
        {
            using var stream = typeof(LocalMappingCache).Assembly.GetManifestResourceStream(name: "games.json");
            if (stream is null)
            {
                logger.LogError("games.json is not embedded in the assembly");
                return false;
            }

            return TryParse(stream, logger, out gameIdToDomain, out gameDomainToId);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Exception reading games.json file");
            return false;
        }
    }

    internal static bool TryParse(
        Stream stream,
        ILogger logger,
        [NotNullWhen(true)] out FrozenDictionary<NexusModsGameId, GameDomain>? gameIdToDomain,
        [NotNullWhen(true)] out FrozenDictionary<GameDomain, NexusModsGameId>? gameDomainToId)
    {
        gameIdToDomain = null;
        gameDomainToId = null;

        try
        {
            var results = JsonSerializer.Deserialize<GameMetadata[]>(stream, SerializerOptions);
            if (results is null || results.Length == 0)
            {
                logger.LogError("games.json contains no entries");
                return false;
            }

            var pairs = results
                .Select(x => (Id: NexusModsGameId.From(x.Id), Domain: GameDomain.From(x.DomainName)))
                .ToArray();

            // The upstream file is not unique per domain: a few domains (e.g. 'neverwinter') belong
            // to two game ids. The right id for such a domain isn't knowable from this file, and the
            // callers (nxm:// links, collection and library lookups) would act on a wrong guess, so
            // ambiguous keys are left out and resolved by the fallback cache, which asks the API.
            gameIdToDomain = ToUnambiguousDictionary(logger, "game id", pairs.Select(tuple => (tuple.Id, tuple.Domain)));
            gameDomainToId = ToUnambiguousDictionary(logger, "domain", pairs.Select(tuple => (tuple.Domain, tuple.Id)));
            return true;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Exception parsing games.json");
            return false;
        }
    }

    private static FrozenDictionary<TKey, TValue> ToUnambiguousDictionary<TKey, TValue>(
        ILogger logger,
        string keyName,
        IEnumerable<(TKey Key, TValue Value)> pairs) where TKey : notnull
    {
        var grouped = pairs.GroupBy(pair => pair.Key).ToArray();

        // The same entry repeated verbatim is redundant, not ambiguous.
        var ambiguous = grouped.Where(group => group.Select(pair => pair.Value).Distinct().Count() > 1).ToArray();
        foreach (var group in ambiguous)
        {
            logger.LogWarning(
                "games.json maps {KeyName} {Key} to multiple values ({Values}), leaving it to the fallback cache",
                keyName, group.Key, string.Join(", ", group.Select(pair => pair.Value).Distinct())
            );
        }

        var ambiguousKeys = ambiguous.Select(group => group.Key).ToHashSet();
        return grouped
            .Where(group => !ambiguousKeys.Contains(group.Key))
            .ToFrozenDictionary(group => group.Key, group => group.First().Value);
    }
}
