using System.Collections.Immutable;
using System.Text;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// Resetting the game (unmanage, clean folder) keeps what the game installed itself and the game's record
/// files, and lets intrinsic files say what they should contain afterwards.
/// </summary>
public class GameInstalledResetTests : AIsolatedGameTest<GameInstalledResetTests, GameInstalledResetStubbedGame>
{
    private const string Source = "StubSource";
    private static readonly LocatorId[] Depot = [LocatorId.From("StubbedGameState.zip")];
    private static readonly GamePath DepotListed = new(LocationId.Game, "bin/originalGameFile.txt");
    private static readonly GamePath Creation = new(LocationId.Game, "Data/alpha.esm");
    private static readonly GamePath PakOutsideTheGameFolder = new(LocationId.Preferences, "paks/gamma.pak");
    private static readonly GamePath CreationsFolder = new(LocationId.Game, "Creations");
    private static readonly GamePath Manifest = new(LocationId.Game, "Creations/alpha.manifest");

    public GameInstalledResetTests(ITestOutputHelper helper) : base(helper) => GameInstalledResetStubbedGame.Reset();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGame<GameInstalledResetStubbedGame>()
            .AddUniversalGameLocator<GameInstalledResetStubbedGame>(Version.Parse("0.0.0"));
    }

    private static FakeGameInstalledRecognizer Recognizer => GameInstalledResetStubbedGame.Recognizer;

    private AbsolutePath OnDisk(GamePath path) => GameInstallation.Locations.ToAbsolutePath(path);

    private async Task WriteOnDisk(GamePath path, string text)
    {
        var file = OnDisk(path);
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(text);
    }

    private static void Recognise(string sourceId, params GamePath[] files)
    {
        Recognizer.Items.RemoveAll(item => item.SourceId == sourceId);
        Recognizer.Items.Add(new GameInstalledContent(Source, sourceId, sourceId, null, GameInstalledConfidence.GameRecord, true, files));
    }

    private async Task Apply(Loadout.ReadOnly loadout) => await Synchronizer.Synchronize(Refresh(loadout));

    private Task Reset() => Synchronizer.ResetToOriginalGameState(GameInstallation, Depot);

    [Fact]
    public async Task AFileTheGameUpdatedKeepsItsDiskCopyOverTheDepotCopy()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk(DepotListed, "updated by the game");
        Recognise("alpha", DepotListed);
        await Apply(loadout);

        await Reset();

        (await OnDisk(DepotListed).ReadAllTextAsync()).Should().Be("updated by the game");
    }

    [Fact]
    public async Task ARecognisedFileOutsideTheGameLocationIsKept()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk(PakOutsideTheGameFolder, "gamma");
        Recognise("gamma", PakOutsideTheGameFolder);
        await Apply(loadout);

        await Reset();

        (await OnDisk(PakOutsideTheGameFolder).ReadAllTextAsync()).Should().Be("gamma");
    }

    [Fact]
    public async Task ATrackedRecordFileUnderARecordFolderIsKept()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk(Manifest, "manifest 1");
        await Apply(loadout);
        Recognizer.Record.Add(CreationsFolder);
        await Apply(loadout);

        await Reset();

        OnDisk(Manifest).FileExists.Should().BeTrue("the manifest belongs to the game and is in no depot");
        (await OnDisk(Manifest).ReadAllTextAsync()).Should().Be("manifest 1");
    }

    [Fact]
    public async Task AFileThatIsNoLongerRecognisedIsRemovedByTheReset()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk(Creation, "alpha 1");
        Recognise("alpha", Creation);
        await Apply(loadout);
        Recognizer.Items.Clear();
        await Apply(loadout);

        await Reset();

        OnDisk(Creation).FileExists.Should().BeFalse("a demoted file is an ordinary external file again");
    }

    [Fact]
    public async Task ARecognisedFileIsInTheDesiredStateTheIntrinsicFileRendersFrom()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk(Creation, "alpha 1");
        Recognise("alpha", Creation);
        await Apply(loadout);

        await Reset();

        var seen = GameInstalledResetStubbedGame.ResetCalls.Should().ContainSingle().Subject;
        seen.Should().ContainKey(Creation);
        seen[Creation].SourceItemType.Should().Be(LoadoutSourceItemType.Game);
        seen[Creation].Loadout.Hash.Should().Be(Encoding.UTF8.GetBytes("alpha 1").xxHash3());
    }

    [Fact]
    public async Task TheBytesRenderedForResetAreOnDiskAfterwards()
    {
        var loadout = await CreateLoadout();
        GameInstalledResetStubbedGame.Rendered = "rendered for reset"u8.ToArray();
        await Apply(loadout);

        await Reset();

        (await OnDisk(GameInstalledResetStubbedGame.IntrinsicPath).ReadAllTextAsync()).Should().Be("rendered for reset");
        (await FileStore.HaveFile(GameInstalledResetStubbedGame.Rendered.xxHash3())).Should().BeTrue();
    }

    [Fact]
    public async Task ANullRenderKeepsTheCopyTheUserHadBefore()
    {
        var file = OnDisk(GameInstalledResetStubbedGame.IntrinsicPath);
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync("the user's own");
        var loadout = await CreateLoadout();
        await Apply(loadout);
        (await file.ReadAllTextAsync()).Should().Be("generated");

        await Reset();

        (await file.ReadAllTextAsync()).Should().Be("the user's own");
    }

    [Fact]
    public async Task UnmanagingLeavesRecognisedFilesAndTheRenderedFile()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk(Creation, "alpha 1");
        Recognise("alpha", Creation);
        GameInstalledResetStubbedGame.Rendered = "rendered for reset"u8.ToArray();
        await Apply(loadout);

        await LoadoutManager.DeactivateCurrentLoadout(GameInstallation);

        (await OnDisk(Creation).ReadAllTextAsync()).Should().Be("alpha 1");
        (await OnDisk(GameInstalledResetStubbedGame.IntrinsicPath).ReadAllTextAsync()).Should().Be("rendered for reset");
    }

    [Fact]
    public async Task UnreadableRecordsKeepWhatTheLastSyncRecognised()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk(DepotListed, "updated by the game");
        Recognise("alpha", DepotListed);
        await Apply(loadout);
        Recognizer.Readable = false;

        await Reset();

        (await OnDisk(DepotListed).ReadAllTextAsync()).Should().Be("updated by the game");
    }

    [Fact]
    public async Task CleaningTheFolderKeepsRecognisedAndRecordFilesAndRemovesDemotedOnes()
    {
        var demoted = new GamePath(LocationId.Game, "Data/beta.esm");
        var loadout = await CreateLoadout();
        await WriteOnDisk(Creation, "alpha 1");
        await WriteOnDisk(demoted, "beta 1");
        await WriteOnDisk(Manifest, "manifest 1");
        Recognise("alpha", Creation);
        Recognise("beta", demoted);
        Recognizer.Record.Add(CreationsFolder);
        await Apply(loadout);
        Recognizer.Items.RemoveAll(item => item.SourceId == "beta");
        await Apply(loadout);

        await LoadoutManager.CleanGameFolder(Refresh(loadout));

        (await OnDisk(Creation).ReadAllTextAsync()).Should().Be("alpha 1");
        (await OnDisk(Manifest).ReadAllTextAsync()).Should().Be("manifest 1");
        OnDisk(demoted).FileExists.Should().BeFalse("a demoted file is an External Change, which Clean folder removes");
    }
}

/// <summary>
/// A stubbed game that recognises the content a test tells it about and generates one preferences file whose
/// reset content a test chooses.
/// </summary>
public class GameInstalledResetStubbedGame : IGame, IGameData<GameInstalledResetStubbedGame>
{
    public static GameId GameId { get; } = GameId.From("GameInstalledResetStubbedGame");
    public static string DisplayName => "Stubbed Game With Reset Content";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(uint.MaxValue - 6);

    public static readonly GamePath IntrinsicPath = new(LocationId.Preferences, "generated.cfg");

    public static FakeGameInstalledRecognizer Recognizer { get; private set; } = new();

    /// <summary>What the intrinsic file renders for a reset; null renders nothing.</summary>
    public static byte[]? Rendered { get; set; }

    /// <summary>The desired state each reset handed to the intrinsic file.</summary>
    public static List<IReadOnlyDictionary<GamePath, SyncNode>> ResetCalls { get; private set; } = [];

    public static void Reset()
    {
        Recognizer = new FakeGameInstalledRecognizer();
        Rendered = null;
        ResetCalls = [];
    }

    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [49u],
        GOGProductIds = [49L],
        EGSCatalogItemId = ["epic-game-id-game-installed-reset"],
    };

    public IStreamFactory IconImage { get; } = new EmbeddedResourceStreamFactory<SilverModManager.StandardGameLocators.TestHelpers.StubbedGames.StubbedGame>("SilverModManager.StandardGameLocators.TestHelpers.Resources.question_mark_game.png");
    public IStreamFactory TileImage => throw new NotImplementedException("No game image for stubbed game.");

    private readonly IServiceProvider _serviceProvider;
    public GameInstalledResetStubbedGame(IServiceProvider provider) => _serviceProvider = provider;

    public GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, "");

    public ILoadoutSynchronizer Synchronizer => new ResetSynchronizer(_serviceProvider);

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        return new Dictionary<LocationId, AbsolutePath>
        {
            { LocationId.Game, gameLocatorResult.Path.Combine("game") },
            { LocationId.Preferences, gameLocatorResult.Path.Combine("preferences") },
        }.ToImmutableDictionary();
    }

    public ILibraryItemInstaller[] LibraryItemInstallers => [];
    public IDiagnosticEmitter[] DiagnosticEmitters => [];
    public ISortOrderManager SortOrderManager => new SortOrderManager(_serviceProvider);

    private sealed class ResetSynchronizer(IServiceProvider provider) : DefaultSynchronizer(provider)
    {
        protected override IGameInstalledContentRecognizer? GameInstalledContent => Recognizer;

        protected override IGamePathFilter GameSpecificPathFilter { get; } = GamePathFilters.Create(path =>
            Recognizer.RecordPaths.Any(record => path == record || path.InFolder(record)));

        public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout) => new() { [IntrinsicPath] = new GeneratedFile() };
    }

    private sealed class GeneratedFile : IIntrinsicFileWithReset
    {
        public GamePath Path => IntrinsicPath;

        public async Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
            => await stream.WriteAsync("generated"u8.ToArray());

        public Task Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx) => Task.CompletedTask;

        public Task<byte[]?> RenderForReset(Loadout.ReadOnly lastSyncedLoadout, IReadOnlyDictionary<GamePath, SyncNode> desiredState)
        {
            ResetCalls.Add(new Dictionary<GamePath, SyncNode>(desiredState));
            return Task.FromResult(Rendered);
        }
    }
}
