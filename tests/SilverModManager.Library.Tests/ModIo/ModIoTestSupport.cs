using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SilverModManager.Library.ModIo;
using SilverModManager.Library.Pickup;
using SilverModManager.Library.Tests.GitHub;
using SilverModManager.Library.Tests.Lock;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.ModIo;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Library.Tests.ModIo;

/// <summary>
/// An <see cref="IModIoApi"/> that answers from tables and records what it was asked.
/// </summary>
internal sealed class FakeModIoApi : IModIoApi
{
    public List<string> Requests { get; } = [];
    public Dictionary<(uint Game, uint Mod), ModIoMod> Mods_ { get; } = [];
    public Dictionary<string, ModIoPage<ModIoMod>> Listings { get; } = [];
    public Dictionary<(uint Game, uint Mod), IReadOnlyList<ModIoFile>> FilesOf { get; } = [];
    public List<ModIoMod> SubscribedMods { get; } = [];

    /// <summary>Thrown by every request when set.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Thrown by Subscribe only when set.</summary>
    public Exception? SubscribeFailure { get; set; }

    private void Record(string request)
    {
        Requests.Add(request);
        if (Failure is not null) throw Failure;
    }

    public Task<ModIoPage<ModIoMod>> Mods(uint gameId, ModIoQuery query, CancellationToken ct)
    {
        var key = $"mods {gameId} [{query.Text}] {query.Sort} {query.Offset}";
        Record(key);
        return Task.FromResult(Listings.TryGetValue(key, out var page) ? page : new ModIoPage<ModIoMod>([], 0, query.Offset));
    }

    public Task<ModIoMod?> Mod(uint gameId, uint modId, CancellationToken ct)
    {
        Record($"mod {gameId} {modId}");
        return Task.FromResult(Mods_.TryGetValue((gameId, modId), out var mod) ? mod : null);
    }

    public Task<IReadOnlyList<ModIoFile>> Files(uint gameId, uint modId, CancellationToken ct)
    {
        Record($"files {gameId} {modId}");
        return Task.FromResult(FilesOf.TryGetValue((gameId, modId), out var files) ? files : []);
    }

    public Task Subscribe(uint gameId, uint modId, bool includeDependencies, CancellationToken ct)
    {
        Record($"subscribe {gameId} {modId} deps={includeDependencies}");
        if (SubscribeFailure is not null) throw SubscribeFailure;
        return Task.CompletedTask;
    }

    public Task Unsubscribe(uint gameId, uint modId, CancellationToken ct)
    {
        Record($"unsubscribe {gameId} {modId}");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ModIoMod>> Subscribed(uint gameId, CancellationToken ct)
    {
        Record($"subscribed {gameId}");
        return Task.FromResult<IReadOnlyList<ModIoMod>>(SubscribedMods);
    }

    public Task<ModIoUser> Me(CancellationToken ct) => Task.FromResult(new ModIoUser(1, "user"));

    public Task<ModIoUser> Me(string token, CancellationToken ct) => Task.FromResult(new ModIoUser(1, "user"));
}

internal static class TestMods
{
    public static ModIoFile File(uint id, string name, byte[]? payload = null, string? md5 = null, bool clean = true, long added = 0, string? version = "1.0", string? binaryUrl = null, uint modId = 0)
        => new(id, modId, added, payload?.Length ?? 100, new ModIoFileHash(md5 ?? (payload is null ? new string('a', 32) : Convert.ToHexStringLower(MD5.HashData(payload)))), name, version,
            new ModIoDownload(binaryUrl ?? $"https://g-6715.modapi.io/v1/games/6715/mods/1/files/{id}/download/hash/{name}", 9_999_999_999), 1, clean ? 0 : 1);

    public static ModIoMod Mod(uint id, string name, long updated, ModIoFile? file, bool adult = false, long downloads = 10)
        => new(id, name, "A summary.", $"https://mod.io/g/test/m/{name.ToLowerInvariant().Replace(' ', '-')}", updated, adult ? 4 : 0,
            new ModIoLogo($"https://thumb.modcdn.io/{id}.png"), new ModIoUserRef("Author"), file, new ModIoStats(downloads));
}

internal sealed class FakeModIoGame(GameId id, uint modIoGameId = 6715, bool syncs = false) : FakeGame(id), IModIoGame
{
    public uint ModIoGameId => modIoGameId;
    public string ModIoNameId => "test-game";
    public bool GameSyncsSubscriptions => syncs;
}

/// <summary>
/// The mod.io pieces over one database, with a clock the test moves and an API it scripts.
/// </summary>
internal sealed class ModIoRig
{
    public FakeModIoApi Api { get; } = new();
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
    public RoutedBytesHandler Bytes { get; } = new();
    public FakeModIoGame Game { get; }
    public GameId GameId { get; }
    public ModIoMods Mods { get; }
    public ExpectedDownloads Expected { get; } = new();
    public IConnection Connection { get; }

    public ModIoRig(IConnection connection, bool syncs = false, uint modIoGameId = 0)
    {
        Connection = connection;
        GameId = GameId.From("Tests.ModIo." + Guid.NewGuid().ToString("N"));
        // Mod ids are only unique inside a game, and the tests share a database, so each rig lists its own game.
        Game = new FakeModIoGame(GameId, modIoGameId == 0 ? (uint)Random.Shared.Next(1_000_000, int.MaxValue) : modIoGameId, syncs);
        Mods = new ModIoMods(Api, Time);
    }

    public HttpClient Client => new(Bytes);

    public async Task<LoadoutId> NewLoadout()
    {
        var loadout = await new LockFixtures(Connection).CreateLoadout();
        using var tx = Connection.BeginTransaction();
        tx.Add(loadout.InstallationId, GameInstallMetadata.Game, GameId);
        await tx.Commit();
        return loadout.LoadoutId;
    }
}
