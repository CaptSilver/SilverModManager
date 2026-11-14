using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NexusMods.Paths;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Library.Tests.GitHub;
using SilverModManager.Library.Workshop;
using SilverModManager.Networking.Steam.Workshop;
using SilverModManager.Networking.Steam.Workshop.DTOs;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Library.Tests.Workshop;

/// <summary>An <see cref="ISteamWorkshopApi"/> that answers from a table and records every call.</summary>
internal sealed class FakeWorkshopApi : ISteamWorkshopApi
{
    public const uint AppId = 268500;

    public List<IReadOnlyList<ulong>> Calls { get; } = [];
    public Dictionary<ulong, PublishedFileDetails> Items { get; } = [];

    /// <summary>Thrown by every call when set.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Every <see cref="Query"/> call, in order.</summary>
    public List<(uint AppId, string Text, string? Cursor, string Key)> QueryCalls { get; } = [];

    /// <summary>The page <see cref="Query"/> answers for a cursor ("" for the first page).</summary>
    public Dictionary<string, WorkshopQueryPage> Pages { get; } = [];

    /// <summary>The only key <see cref="IsKeyAccepted"/> and <see cref="Query"/> accept, when set.</summary>
    public string? AcceptedKey { get; set; }

    public Task<WorkshopQueryPage> Query(uint appId, string text, string? cursor, string key, CancellationToken ct)
    {
        QueryCalls.Add((appId, text, cursor, key));
        if (Failure is not null) throw Failure;
        if (AcceptedKey is not null && key != AcceptedKey) throw new SteamWebApiKeyRejectedException();
        return Task.FromResult(Pages.TryGetValue(cursor ?? "", out var page) ? page : new WorkshopQueryPage([], null, 0));
    }

    public Task<bool> IsKeyAccepted(string key, CancellationToken ct) => Task.FromResult(AcceptedKey is null || key == AcceptedKey);

    public Task<IReadOnlyList<PublishedFileDetails>> Details(IReadOnlyList<ulong> ids, CancellationToken ct)
    {
        Calls.Add(ids);
        if (Failure is not null) throw Failure;
        return Task.FromResult<IReadOnlyList<PublishedFileDetails>>([.. ids.Select(id => Items.TryGetValue(id, out var item) ? item : new PublishedFileDetails(id, 9))]);
    }

    public void Add(ulong id, string title, long updated, uint appId = AppId)
        => Items[id] = new PublishedFileDetails(id, 1, title, DateTimeOffset.FromUnixTimeSeconds(updated), 100, null, appId, 0, 0);
}

internal sealed class FakeWorkshopGame(GameId id, uint appId = FakeWorkshopApi.AppId, bool hasOrderFile = true) : FakeGame(id), ISteamWorkshopGame
{
    public uint SteamWorkshopAppId => appId;
    public bool HasOrderFile => hasOrderFile;
}

/// <summary>A Steam library on disk with the game's folder and whatever the test subscribes to.</summary>
internal sealed class SteamLibraryFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "workshop-view-" + Guid.NewGuid().ToString("N"));

    public SteamLibraryFixture() => Directory.CreateDirectory(GameFolder);

    public string GameFolder => Path.Combine(_root, "steamapps", "common", "XCOM 2");

    public AbsolutePath Game => FileSystem.Shared.FromUnsanitizedFullPath(GameFolder);

    public string Subscribe(ulong id, long folderModified, uint appId = FakeWorkshopApi.AppId)
    {
        var folder = Path.Combine(_root, "steamapps", "workshop", "content", appId.ToString(), id.ToString());
        Directory.CreateDirectory(folder);
        Directory.SetLastWriteTimeUtc(folder, DateTimeOffset.FromUnixTimeSeconds(folderModified).UtcDateTime);
        return folder;
    }

    public GameInstallation Install(IGameData game)
    {
        var locations = GameLocations.Create(new Dictionary<LocationId, AbsolutePath> { [LocationId.Game] = Game }.ToImmutableDictionary());
        var locatorResult = new GameLocatorResult
        {
            Game = game,
            Path = Game,
            Store = GameStore.Steam,
            StoreIdentifier = "268500",
            LocatorIds = [],
            Locator = null!,
        };
        return new GameInstallation(locatorResult, locations);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

internal sealed class WorkshopRig : IDisposable
{
    public FakeWorkshopApi Api { get; } = new();
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
    public SteamLibraryFixture Library { get; } = new();
    public FakeWorkshopGame Game { get; } = new(GameId.From("Tests.Workshop." + Guid.NewGuid().ToString("N")));
    public WorkshopItemsView View { get; }
    public InMemorySecretStore Secrets { get; } = new();

    public WorkshopRig() => View = new WorkshopItemsView(Api, Time, SteamWorkshopSettings.Default, NullLogger<WorkshopItemsView>.Instance);

    public GameInstallation Install => Library.Install(Game);

    public void Dispose() => Library.Dispose();
}
