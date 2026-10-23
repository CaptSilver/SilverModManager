using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.Emitters;
using SilverModManager.Games.Generic.Diagnostics;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests;

/// <summary>A title that overrides only its data folder: everything else is the base's default.</summary>
public class TestBethesdaGame(IServiceProvider provider) : ABethesdaGame<TestBethesdaGame>(provider), IGameData<TestBethesdaGame>
{
    public static GameId GameId { get; } = GameId.From("CreationEngine.TestBethesda");
    public static string DisplayName => "Test Bethesda";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(99001);

    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId) { SteamAppIds = [99001u] };
    protected override RelativePath PrimaryExe => "Test.exe";
    protected override string[] GameAliases => ["Test Bethesda"];
    protected override string DataFolderName => "Data Files";
}

/// <summary>A title that keeps its saves in the game folder and its engine files in Bin.</summary>
public class RelocatedTestGame(IServiceProvider provider) : ABethesdaGame<RelocatedTestGame>(provider), IGameData<RelocatedTestGame>
{
    public static GameId GameId { get; } = GameId.From("CreationEngine.RelocatedTest");
    public static string DisplayName => "Relocated Test";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(99002);

    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId) { SteamAppIds = [99002u] };
    protected override RelativePath PrimaryExe => "Test.exe";
    protected override string[] GameAliases => ["Relocated Test"];
    protected override string[] EngineFiles => [@"loader\.exe"];
    protected override GamePath EnginePath => new(LocationId.Game, "Bin");
    public override IReadOnlyList<GamePath> ProtectedFolders => [new GamePath(LocationId.Game, "Saves")];
}

public class BethesdaBaseTests(ITestOutputHelper helper)
    : ABethesdaInstallerRoutingTest<BethesdaBaseTests, TestBethesdaGame>(helper)
{
    protected override Version GameVersion => new("1.0.0");

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services).AddGame<TestBethesdaGame>();

    [Fact]
    public void WithoutFolderNames_OnlyTheGameLocationExists()
    {
        var fs = new InMemoryFileSystem();
        var result = new GameLocatorResult
        {
            Game = Game,
            Path = fs.FromUnsanitizedFullPath("/games/test"),
            Store = GameStore.Steam,
            StoreIdentifier = "id",
            LocatorIds = [],
            Locator = null!,
        };

        Game.GetLocations(fs, result).Keys.Should().Equal(LocationId.Game);
    }

    [Fact]
    public void DataPathFollowsTheDataFolderName() => Game.DataPath.Should().Be(new GamePath(LocationId.Game, "Data Files"));

    [Fact]
    public async Task TheDataFolderNameReachesTheInstaller()
    {
        (await InstallLayout("meshes/x.nif")).Should().Equal("Game/Data Files/meshes/x.nif");
        (await InstallLayout("Data Files/y.esp")).Should().Equal("Game/Data Files/y.esp");
    }

    [Fact]
    public async Task AGameWithNoEngineFilesSendsPlainFilesToData() =>
        (await InstallLayout("readme.txt", "loader.exe")).Should().Equal("Game/Data Files/loader.exe", "Game/Data Files/readme.txt");

    [Fact]
    public void OnlyTheProtonPrefixCheckIsEmitted() => Game.DiagnosticEmitters.Select(e => e.GetType()).Should().Equal(typeof(ProtonPrefixEmitter));

    [Fact]
    public void NoIntrinsicFilesAreOffered() => ((ALoadoutSynchronizer)Game.Synchronizer).IntrinsicFiles(default).Should().BeEmpty();

    [Fact]
    public void SavesUnderPreferencesAreFilteredByDefault()
    {
        var filter = ((ALoadoutSynchronizer)Game.Synchronizer).GamePathFilter;
        filter.ShouldFilter(new GamePath(LocationId.Preferences, "Saves/x")).Should().BeTrue();
    }

    [Fact]
    public void OverridingProtectedFoldersReplacesTheDefault()
    {
        var game = new RelocatedTestGame(ServiceProvider);
        var filter = ((ALoadoutSynchronizer)game.Synchronizer).GamePathFilter;

        filter.ShouldFilter(new GamePath(LocationId.Game, "Saves/x")).Should().BeTrue();
        filter.ShouldFilter(new GamePath(LocationId.Preferences, "Saves/x")).Should().BeFalse();
    }
}

public class RelocatedEngineFilesTests(ITestOutputHelper helper)
    : ABethesdaInstallerRoutingTest<RelocatedEngineFilesTests, RelocatedTestGame>(helper)
{
    protected override Version GameVersion => new("1.0.0");

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services).AddGame<RelocatedTestGame>();

    [Fact]
    public async Task EngineFilesLandAtTheOverriddenEnginePath() =>
        (await InstallLayout("loader.exe")).Should().Equal("Game/Bin/loader.exe");
}
