using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games.FileHashes.Models;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Hashes;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Games.FileHashes.Tests;

/// <summary>
/// The service's lookups with a downloaded database on disk and rows in the local store: file
/// lookups prefer the local store per locator id, version names prefer the downloaded one.
/// </summary>
public sealed class LookupsReadBothStoresTests : IDisposable
{
    private readonly ServiceHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task HashesName_ResolvesToTheRealDatabaseOnceOpened()
    {
        var location = _harness.NewDatabaseLocation();
        var service = _harness.CreateService(location);
        var main = _harness.Host.OpenInMemory("main");
        var loadout = await HashRows.AddLoadout(main, GameStore.Steam, "/games/test", "1001");
        // Nothing is on disk yet, so the placeholder is what answers to "hashes".
        LoadoutFiles(main, loadout).Should().BeEmpty();
        await _harness.CreateDatabaseOnDisk(location, 1000, c => HashRows.AddSteamManifest(c, 1001, ("a.esm", "central file")));

        await service.CheckForUpdate();

        var names = main.Query<string>($"SELECT Name FROM mnemonicdb_active_connections()");
        names.Count(n => n.Equals("hashes", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
        LoadoutFiles(main, loadout).Select(r => r.Path.ToString()).Should().BeEquivalentTo(["a.esm"]);
    }

    [Fact]
    public async Task LoadoutFiles_WithNoCentralDatabase_StillBinds()
    {
        var location = _harness.NewDatabaseLocation();
        // The embedded database is extracted into this directory, which it cannot be if nothing can be written to it.
        _harness.MakeReadOnly(location);
        var logger = new RecordingLogger<FileHashesService>();
        var service = _harness.CreateService(location, logger);
        await service.CheckForUpdate();
        logger.Messages.Should().Contain(m => m.Contains("Failed to add embedded database"), "the extraction has to fail for this test to mean anything");
        await HashRows.AddSteamManifest(service.Local.Connection, 1002, ("b.esm", "local file"));
        var main = _harness.Host.OpenInMemory("main");
        var loadout = await HashRows.AddLoadout(main, GameStore.Steam, "/games/test", "1002");

        var rows = LoadoutFiles(main, loadout);

        rows.Select(r => r.Path.ToString()).Should().BeEquivalentTo(["b.esm"]);
        service.Databases.Should().ContainSingle("the placeholder is not a store lookups read");
        service.Current.Connection.Should().BeSameAs(service.Local.Connection);
    }

    [Fact]
    public void Dispose_WithNoDatabaseConnected_UnregistersTheHashesName()
    {
        var service = _harness.CreateService(_harness.NewDatabaseLocation());
        var main = _harness.Host.OpenInMemory("main");

        _harness.DisposeService(service);

        var names = main.Query<string>($"SELECT Name FROM mnemonicdb_active_connections()");
        names.Should().NotContain(n => n.Equals("hashes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetGameFiles_FindsAManifestOnlyInTheLocalStore()
    {
        var service = await ServiceWithCentral(c => HashRows.AddSteamManifest(c, 1001, ("a.esm", "central file")));
        await HashRows.AddSteamManifest(service.Local.Connection, 1002, ("b.esm", "local file"));

        var files = service.GetGameFiles((GameStore.Steam, [LocatorId.From("1002")]));

        files.Select(f => f.Path.Path.ToString()).Should().BeEquivalentTo(["b.esm"]);
    }

    [Fact]
    public async Task GetGameFiles_TwoLocatorIds_EachAnsweredByTheStoreThatHasIt()
    {
        var service = await ServiceWithCentral(c => HashRows.AddSteamManifest(c, 1001, ("a.esm", "central file")));
        await HashRows.AddSteamManifest(service.Local.Connection, 1002, ("b.esm", "local file"));

        var files = service.GetGameFiles((GameStore.Steam, [LocatorId.From("1001"), LocatorId.From("1002")]));

        files.Select(f => f.Path.Path.ToString()).Should().BeEquivalentTo(["a.esm", "b.esm"]);
    }

    [Fact]
    public async Task GetGameFiles_ManifestInBothStores_IsAnsweredByTheLocalOne()
    {
        // The local store is written from the install's own data, so its manifest is the one that describes the disk.
        var service = await ServiceWithCentral(c => HashRows.AddSteamManifest(c, 1001, ("a.esm", "a"), ("b.esm", "b")));
        await HashRows.AddSteamManifest(service.Local.Connection, 1001, ("a.esm", "a"));

        var files = service.GetGameFiles((GameStore.Steam, [LocatorId.From("1001")]));

        files.Select(f => f.Path.Path.ToString()).Should().BeEquivalentTo(["a.esm"]);
    }

    [Fact]
    public async Task GetGameFiles_GogBuildsInEitherStore_AreFound()
    {
        var service = await ServiceWithCentral(c => HashRows.AddGogBuild(c, 100, ("a.esm", "central file")));
        await HashRows.AddGogBuild(service.Local.Connection, 200, ("b.esm", "local file"));

        var files = service.GetGameFiles((GameStore.GOG, [LocatorId.From("100"), LocatorId.From("200")]));

        files.Select(f => f.Path.Path.ToString()).Should().BeEquivalentTo(["a.esm", "b.esm"]);
    }

    [Fact]
    public async Task TryGetVanityVersion_UsesALocalVersionDefinitionWhenTheCentralStoreLacksTheBuild()
    {
        var service = await ServiceWithCentral(c => HashRows.AddSteamManifest(c, 1001, ("a.esm", "central file")));
        await HashRows.AddSteamManifest(service.Local.Connection, 1002, ("b.esm", "local file"));
        await HashRows.AddSteamVersionDefinition(service.Local.Connection, "1234567", 1002);

        var found = service.TryGetVanityVersion((GameStore.Steam, [LocatorId.From("1002")]), out var version);

        found.Should().BeTrue();
        version.Value.Should().Be("1234567");
    }

    [Fact]
    public async Task TryGetVanityVersion_PrefersTheCentralNameWhenBothStoresKnowTheBuild()
    {
        var service = await ServiceWithCentral(async c =>
        {
            await HashRows.AddSteamManifest(c, 1001, ("a.esm", "a"));
            await HashRows.AddSteamVersionDefinition(c, "1.6.1170", 1001);
        });
        await HashRows.AddSteamManifest(service.Local.Connection, 1001, ("a.esm", "a"));
        await HashRows.AddSteamVersionDefinition(service.Local.Connection, "1234567", 1001);

        var found = service.TryGetVanityVersion((GameStore.Steam, [LocatorId.From("1001")]), out var version);

        found.Should().BeTrue();
        version.Value.Should().Be("1.6.1170");
    }

    [Fact]
    public async Task TryGetLocatorIdsForVanityVersion_FallsBackToTheLocalStore()
    {
        var service = await ServiceWithCentral(c => HashRows.AddSteamManifest(c, 1001, ("a.esm", "a")));
        await HashRows.AddSteamManifest(service.Local.Connection, 1002, ("b.esm", "b"));
        await HashRows.AddSteamVersionDefinition(service.Local.Connection, "1234567", 1002);

        var found = service.TryGetLocatorIdsForVanityVersion(GameStore.Steam, VanityVersion.From("1234567"), out var ids);

        found.Should().BeTrue();
        ids.Select(id => id.Value).Should().BeEquivalentTo(["1002"]);
    }

    [Fact]
    public async Task GetKnownVanityVersions_ListsEachNameOnceAcrossBothStores()
    {
        var service = await ServiceWithCentral(async c =>
        {
            await HashRows.AddSteamManifest(c, 1001, ("a.esm", "a"));
            await HashRows.AddSteamVersionDefinition(c, "1.6.1170", 1001);
            await HashRows.AddSteamVersionDefinition(c, "shared", 1001);
        });
        await HashRows.AddSteamManifest(service.Local.Connection, 1002, ("b.esm", "b"));
        await HashRows.AddSteamVersionDefinition(service.Local.Connection, "1234567", 1002);
        await HashRows.AddSteamVersionDefinition(service.Local.Connection, "shared", 1002);

        var names = service.GetKnownVanityVersions(HashRows.GameId).Select(v => v.Value);

        names.Should().BeEquivalentTo(["1.6.1170", "shared", "1234567"]);
    }

    [Fact]
    public async Task SuggestVersionData_ConsultsBothStores()
    {
        var service = await ServiceWithCentral(async c =>
        {
            await HashRows.AddSteamManifest(c, 1001, ("a.esm", "central file"));
            await HashRows.AddSteamVersionDefinition(c, "1.6.1170", 1001);
        });
        await HashRows.AddSteamManifest(service.Local.Connection, 1002, ("b.esm", "local file"));
        await HashRows.AddSteamVersionDefinition(service.Local.Connection, "1234567", 1002);
        var onDisk = new[] { (new GamePath(LocationId.Game, "b.esm"), await HashOf("local file")) };

        var suggestion = service.SuggestVersionData(Installation(GameStore.Steam), onDisk);

        suggestion.HasValue.Should().BeTrue();
        suggestion.Value.VanityVersion.Value.Should().Be("1234567");
        suggestion.Value.LocatorIds.Select(id => id.Value).Should().BeEquivalentTo(["1002"]);
    }

    [Fact]
    public async Task SuggestVersionData_GameWithoutNexusId_SuggestsNothing()
    {
        var service = await ServiceWithCentral(async c =>
        {
            await HashRows.AddSteamManifest(c, 1001, ("a.esm", "central file"));
            await HashRows.AddSteamVersionDefinition(c, "1.6.1170", 1001);
        });
        var onDisk = new[] { (new GamePath(LocationId.Game, "a.esm"), await HashOf("central file")) };

        var suggestion = service.SuggestVersionData(Installation(GameStore.Steam, withNexusId: false), onDisk);

        suggestion.HasValue.Should().BeFalse("version definitions are filed under Nexus ids, and this game has none");
    }

    private async Task<FileHashesService> ServiceWithCentral(Func<IConnection, Task> fill)
    {
        var location = _harness.NewDatabaseLocation();
        await _harness.CreateDatabaseOnDisk(location, 1000, fill);
        var service = _harness.CreateService(location);
        await service.CheckForUpdate();
        return service;
    }

    private static GameInstallation Installation(GameStore store, bool withNexusId = true)
    {
        var game = Substitute.For<IGameData>();
        game.NexusModsGameId.Returns(withNexusId ? DynamicData.Kernel.Optional.Some(HashRows.GameId) : DynamicData.Kernel.Optional<NexusModsGameId>.None);
        var locatorResult = new GameLocatorResult
        {
            Game = game,
            Path = FileSystem.Shared.FromUnsanitizedFullPath("/games/test"),
            Store = store,
            StoreIdentifier = "1",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
        };
        return new GameInstallation(locatorResult, default);
    }

    private static List<(RelativePath Path, Hash Hash, Size Size)> LoadoutFiles(IConnection main, EntityId loadout)
    {
        return main.Query<(RelativePath Path, Hash Hash, Size Size)>(
            $"SELECT Path, Hash, Size FROM file_hashes.loadout_files({main.Db}) WHERE Loadout = {loadout.Value}"
        ).ToList();
    }

    private static async Task<Hash> HashOf(string content)
        => (await MultiHasher.HashStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)))).XxHash3;
}

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Messages.Add(formatter(state, exception));
}
