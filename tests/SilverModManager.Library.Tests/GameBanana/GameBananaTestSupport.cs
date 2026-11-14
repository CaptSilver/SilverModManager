using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Library.GameBanana;
using SilverModManager.Library.Lock;
using SilverModManager.Library.Tests.GitHub;
using SilverModManager.Library.Tests.Lock;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.GameBanana;
using SilverModManager.Networking.GameBanana.DTOs;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Library.Tests.GameBanana;

/// <summary>
/// An <see cref="IGameBananaApi"/> that answers from tables and records what it was asked.
/// </summary>
internal sealed class FakeGameBananaApi : IGameBananaApi
{
    public const int PerPageSeen = 0;

    public List<string> Requests { get; } = [];
    public Dictionary<long, GameBananaProfile> Profiles { get; } = [];
    public Dictionary<string, GameBananaListing> Listings { get; } = [];

    /// <summary>Thrown by every request when set.</summary>
    public Exception? Failure { get; set; }

    private void Record(string request)
    {
        Requests.Add(request);
        if (Failure is not null) throw Failure;
    }

    private GameBananaListing ListingFor(string key) => Listings.TryGetValue(key, out var listing) ? listing : new GameBananaListing([], 0, true);

    public Task<GameBananaListing> Subfeed(int gameId, int page, int perPage, CancellationToken ct)
    {
        var key = $"subfeed {gameId} {page}";
        Record(key);
        return Task.FromResult(ListingFor(key));
    }

    public Task<GameBananaListing> Index(int gameId, GameBananaSection section, string sort, int page, int perPage, CancellationToken ct)
    {
        var key = $"index {gameId} {section} {sort} {page}";
        Record(key);
        return Task.FromResult(ListingFor(key));
    }

    public Task<GameBananaListing> Search(int gameId, string text, int page, CancellationToken ct)
    {
        var key = $"search {gameId} {text} {page}";
        Record(key);
        return Task.FromResult(ListingFor(key));
    }

    public Task<GameBananaProfile> Profile(long modId, CancellationToken ct)
    {
        Record($"profile {modId}");
        return Profiles.TryGetValue(modId, out var profile) ? Task.FromResult(profile) : throw new GameBananaUnavailableException(modId);
    }
}

internal static class TestMods
{
    public static GameBananaFile File(long id, string name, string? md5 = null, bool clean = true, long added = 0, IReadOnlyDictionary<string, IReadOnlyList<string>>? warnings = null)
        => new(id, name, 100, $"https://gamebanana.com/dl/{id}", md5 ?? new string('a', 32), clean ? "clean" : "infected", added, 5, warnings);

    public static GameBananaProfile Profile(
        long id, string name, long? updated, long modified, IReadOnlyList<GameBananaFile> files, string version = "",
        int gameId = 297, GameBananaLicenseChecklist? license = null, IReadOnlyList<GameBananaAlternateSource>? mirrors = null)
        => new(id, name, false, false, $"https://gamebanana.com/mods/{id}", modified, updated, version, files, mirrors, license ?? new GameBananaLicenseChecklist(["Download and install this Mod"], [], []),
            new GameBananaGameRef(gameId, "Team Fortress 2"), new GameBananaSubmitter("Author"), new GameBananaCategory("Maps"));

    public static GameBananaRecord Record(long id, string name, string model = "Mod", bool adult = false, long? updated = 1000, string version = "1.0")
        => new(id, name, model, $"https://gamebanana.com/mods/{id}", 500, 900, updated, true, version, adult, 10, 2,
            new GameBananaSubmitter("Author"), new GameBananaPreviewMedia([new GameBananaImage("https://images.gamebanana.com/img/ss/mods", $"{id}.jpg", $"220-90_{id}.jpg")]),
            new GameBananaGameRef(297, "Team Fortress 2"), new GameBananaCategory("Maps"));
}

internal sealed class FakeGameBananaGame(GameId id, IReadOnlyList<GameBananaSection>? sections = null, int gameBananaId = 297) : FakeGame(id), IGameBananaGame
{
    public int GameBananaGameId => gameBananaId;
    public IReadOnlyList<GameBananaSection> GameBananaSections => sections ?? [GameBananaSection.Mod];
}

/// <summary>
/// The GameBanana pieces over one database, with a clock the test moves and an API it scripts.
/// </summary>
internal sealed class GameBananaRig
{
    public FakeGameBananaApi Api { get; } = new();
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
    public RoutedBytesHandler Bytes { get; } = new();
    public FakeGameBananaGame Game { get; }
    public GameId GameId { get; }
    public GameBananaProfiles Profiles { get; }
    public IConnection Connection { get; }

    public GameBananaRig(IConnection connection, IReadOnlyList<GameBananaSection>? sections = null, int gameBananaId = 297)
    {
        Connection = connection;
        GameId = GameId.From("Tests.GameBanana." + Guid.NewGuid().ToString("N"));
        Game = new FakeGameBananaGame(GameId, sections, gameBananaId);
        Profiles = new GameBananaProfiles(Api, Time, NullLogger<GameBananaProfiles>.Instance);
    }

    public HttpClient Client => new(Bytes);

    /// <summary>
    /// A loadout of this rig's game, which is how an install learns which game the file is for.
    /// </summary>
    public async Task<LoadoutId> NewLoadout()
    {
        var loadout = await new LockFixtures(Connection).CreateLoadout();
        using var tx = Connection.BeginTransaction();
        tx.Add(loadout.InstallationId, GameInstallMetadata.Game, GameId);
        await tx.Commit();
        return loadout.LoadoutId;
    }
}
