using System.Collections.Immutable;
using System.Text;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A loadout that owns the game's INIs: the first copy comes from disk, every write is the baseline with the enabled
/// tweaks and the app's own keys on top, and what was written is recorded.
/// </summary>
public class GameIniBakeTests(ITestOutputHelper helper) : AIsolatedGameTest<GameIniBakeTests, IniStubbedGame>(helper)
{
    private static readonly byte[] UserIni = "; my settings\r\n[Display]\r\niSize W=2560 ; wide\r\niSize H=1440\r\n\r\n[General]\r\nsLanguage=ENGLISH\r\n"u8.ToArray();

    private IniStubbedGame Stub => (IniStubbedGame)GameInstallation.Game;

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddGame<IniStubbedGame>()
        .AddUniversalGameLocator<IniStubbedGame>(Version.Parse("0.0.0"));

    private AbsolutePath Live(GamePath path) => GameInstallation.Locations.ToAbsolutePath(path);

    private async Task WriteLive(GamePath path, byte[] bytes)
    {
        Live(path).Parent.CreateDirectory();
        await Live(path).WriteAllBytesAsync(bytes);
    }

    private async Task<string> ReadLive(GamePath path) => Encoding.UTF8.GetString(await Live(path).ReadAllBytesAsync());

    private async Task<Loadout.ReadOnly> Prepare(Loadout.ReadOnly loadout) => await GameInis.Prepare(Stub, Refresh(loadout), ServiceProvider);

    private async Task<Loadout.ReadOnly> Apply(Loadout.ReadOnly loadout) => await Synchronizer.Synchronize(Refresh(loadout));

    private LoadoutIniState.ReadOnly[] States(Loadout.ReadOnly loadout) => LoadoutIniState.FindByLoadout(Connection.Db, loadout.LoadoutId).ToArray();

    private async Task KeepSaves(Loadout.ReadOnly loadout, bool value)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(loadout.Id, Loadout.KeepSavesWithLoadout, value);
        await tx.Commit();
    }

    private async Task OwnInis(Loadout.ReadOnly loadout, bool value)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(loadout.Id, Loadout.OwnsGameInis, value);
        await tx.Commit();
    }

    private async Task<LoadoutItemGroupId> AddTweakMod(Loadout.ReadOnly loadout, string modName, string tweakFile, string content, ulong priority, bool disabled = false)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(tweakFile, new MemoryStream(bytes)), bytes.xxHash3(), Size.FromLong(bytes.Length))]);

        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, modName);
        AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, $"INI Tweaks/{tweakFile}"), content);
        _ = new LoadoutItemGroupPriority.New(tx)
        {
            LoadoutId = loadout.LoadoutId,
            TargetId = group,
            Priority = ConflictPriority.From(priority),
        };
        if (disabled) tx.Add(group.Value, LoadoutItem.Disabled, Null.Instance);
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[group.Value]);
    }

    [Fact]
    public async Task TheFirstBakeKeepsTheLiveFileAsTheBaselineAndLeavesItAlone()
    {
        var loadout = await CreateLoadout();
        await WriteLive(IniStubbedGame.MainIni, UserIni);

        loadout = await Prepare(loadout);

        var state = States(loadout).Should().ContainSingle().Subject;
        ((GamePath)state.Path).Should().Be(IniStubbedGame.MainIni);
        state.Baseline.Should().Be(UserIni.xxHash3());
        (await FileStore.Load(state.Baseline)).Should().Equal(UserIni);
        (await Live(IniStubbedGame.MainIni).ReadAllBytesAsync()).Should().Equal(UserIni, "nothing is written before the sync writes");
        Live(IniStubbedGame.MainIni).Parent.EnumerateFiles().Select(f => f.FileName.ToString()).Should().HaveCount(1, "no .bak is left beside the INI, the next ingest would pick it up");
    }

    [Fact]
    public async Task AnIniThatIsNotOnDiskGetsNoRow()
    {
        var loadout = await CreateLoadout();

        loadout = await Prepare(loadout);

        States(loadout).Should().BeEmpty();
        GameInis.IntrinsicFiles(Stub, loadout, ServiceProvider).Should().BeEmpty();
    }

    [Fact]
    public async Task ASecondPrepareAddsNothing()
    {
        var loadout = await CreateLoadout();
        await WriteLive(IniStubbedGame.MainIni, UserIni);
        loadout = await Prepare(loadout);
        var before = Connection.Db.BasisTxId;

        loadout = await Prepare(loadout);

        Connection.Db.BasisTxId.Should().Be(before, "the baseline and the recorded bake are already there");
        States(loadout).Should().ContainSingle();
    }

    [Fact]
    public async Task TheWriteKeepsTheLayoutOfUntouchedLinesAndAddsTheAppsKey()
    {
        var loadout = await CreateLoadout();
        await WriteLive(IniStubbedGame.MainIni, UserIni);

        await Apply(loadout);

        var text = await ReadLive(IniStubbedGame.MainIni);
        text.Should().StartWith(Encoding.UTF8.GetString(UserIni), "every line the bake did not touch keeps its bytes, comment and line ending");
        text.Should().Contain("[Archive]\r\nbInvalidateOlderFiles=1\r\n");
    }

    [Fact]
    public async Task TheBakedBytesAreRecordedAndStored()
    {
        var loadout = await CreateLoadout();
        await WriteLive(IniStubbedGame.MainIni, UserIni);

        loadout = await Apply(loadout);

        var written = await Live(IniStubbedGame.MainIni).ReadAllBytesAsync();
        var state = States(Refresh(loadout)).Should().ContainSingle().Subject;
        var baked = LoadoutIniState.LastBakedHash.Get(state);
        baked.Should().Be(written.xxHash3());
        (await FileStore.Load(baked)).Should().Equal(written, "an ingest diffs the live file against these bytes");
        state.Baseline.Should().Be(UserIni.xxHash3(), "the baseline stays the player's copy");
    }

    [Fact]
    public async Task EnabledTweaksAreMergedLowestPriorityFirstAndTheLoadoutTweakLast()
    {
        var loadout = await CreateLoadout();
        await WriteLive(IniStubbedGame.MainIni, UserIni);
        await AddTweakMod(loadout, "High", "High [Game.ini].ini", "[Display]\r\niSize W=1920\r\n[Extra]\r\nfromHigh=1\r\n", priority: 20);
        await AddTweakMod(loadout, "Low", "Low [Game.ini].ini", "[Display]\r\niSize W=800\r\niSize H=600\r\n", priority: 10);
        await AddTweakMod(loadout, "Elsewhere", "Other [GamePrefs.ini].ini", "[Display]\r\niSize H=1\r\n", priority: 30);
        var settings = await AddTweakMod(loadout, "Game settings", "Loadout [Game.ini].ini", "[Display]\r\niSize H=900\r\n", priority: 5);
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(settings.Value, LoadoutItemGroup.GameSettingsGroup, Null.Instance);
            await tx.Commit();
        }

        await Apply(loadout);

        var baked = IniDocument.Parse(await Live(IniStubbedGame.MainIni).ReadAllBytesAsync());
        baked.Get("Display", "iSize W").Should().Be("1920", "the higher priority mod wins over the lower one");
        baked.Get("Display", "iSize H").Should().Be("900", "the loadout's own tweak is merged last, and a tweak for another file is ignored");
        baked.Get("Extra", "fromHigh").Should().Be("1");
        baked.Get("General", "sLanguage").Should().Be("ENGLISH");
    }

    [Fact]
    public async Task ADisabledModsTweakIsNotMerged()
    {
        var loadout = await CreateLoadout();
        await WriteLive(IniStubbedGame.MainIni, UserIni);
        await AddTweakMod(loadout, "Off", "Off [Game.ini].ini", "[Display]\r\niSize W=1\r\n", priority: 10, disabled: true);

        await Apply(loadout);

        IniDocument.Parse(await Live(IniStubbedGame.MainIni).ReadAllBytesAsync()).Get("Display", "iSize W").Should().StartWith("2560");
    }

    [Fact]
    public async Task TheSaveRedirectKeyIsWrittenOnlyWhileTheLoadoutKeepsItsSaves()
    {
        var loadout = await CreateLoadout();
        await WriteLive(IniStubbedGame.MainIni, UserIni);
        var key = $"Saves\\{LoadoutSaveSlot.IdOf(loadout)}\\";

        await Apply(loadout);
        IniDocument.Parse(await Live(IniStubbedGame.MainIni).ReadAllBytesAsync()).Get("General", "sLocalSavePath").Should().BeNull();

        await KeepSaves(loadout, true);
        await Apply(loadout);
        IniDocument.Parse(await Live(IniStubbedGame.MainIni).ReadAllBytesAsync()).Get("General", "sLocalSavePath").Should().Be(key);

        await KeepSaves(loadout, false);
        await Apply(loadout);
        IniDocument.Parse(await Live(IniStubbedGame.MainIni).ReadAllBytesAsync()).Get("General", "sLocalSavePath").Should().BeNull("the key goes when the toggle does");
    }

    [Fact]
    public async Task ResetRendersTheBaselineAlone()
    {
        var loadout = await CreateLoadout();
        await WriteLive(IniStubbedGame.MainIni, UserIni);
        await AddTweakMod(loadout, "Low", "Low [Game.ini].ini", "[Display]\r\niSize W=800\r\n", priority: 10);
        loadout = await Apply(loadout);

        var file = (IIntrinsicFileWithReset)((ALoadoutSynchronizer)Synchronizer).IntrinsicFiles(Refresh(loadout))[IniStubbedGame.MainIni];
        var rendered = await file.RenderForReset(Refresh(loadout), new Dictionary<GamePath, SyncNode>());

        rendered.Should().Equal(UserIni);
    }

    [Fact]
    public async Task ALoadoutThatDoesNotOwnTheInisLeavesThemToTheGame()
    {
        var loadout = await CreateLoadout();
        await OwnInis(loadout, false);
        await WriteLive(IniStubbedGame.MainIni, UserIni);

        loadout = await Prepare(loadout);
        loadout = await Apply(loadout);

        States(loadout).Should().BeEmpty("no row is created, so there is no baseline to bake from");
        (await Live(IniStubbedGame.MainIni).ReadAllBytesAsync()).Should().Equal(UserIni);
    }

    [Fact]
    public async Task ALoadoutThatNeverSetTheFlagOwnsTheInis()
    {
        var loadout = await CreateLoadout();
        loadout.Contains(Loadout.OwnsGameInis).Should().BeFalse("nothing wrote the flag");
        await WriteLive(IniStubbedGame.MainIni, UserIni);

        loadout = await Prepare(loadout);

        States(loadout).Should().ContainSingle();
    }

    [Fact]
    public async Task TurningOwnershipOffAfterABakeLeavesTheLiveFileInPlace()
    {
        var loadout = await CreateLoadout();
        await WriteLive(IniStubbedGame.MainIni, UserIni);
        loadout = await Apply(loadout);
        var baked = await Live(IniStubbedGame.MainIni).ReadAllBytesAsync();

        await OwnInis(loadout, false);
        await Apply(loadout);

        Live(IniStubbedGame.MainIni).FileExists.Should().BeTrue("the game still needs its INI");
        (await Live(IniStubbedGame.MainIni).ReadAllBytesAsync()).Should().Equal(baked);
    }

    [Fact]
    public async Task AnIniAnEarlierApplyIngestedIsBakedOverOnceTheLoadoutOwnsIt()
    {
        var loadout = await CreateLoadout();
        await OwnInis(loadout, false);
        await WriteLive(IniStubbedGame.MainIni, UserIni);
        loadout = await Apply(loadout);
        LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemWithTargetPath()
            .Where(item => (GamePath)item.TargetPath == IniStubbedGame.MainIni)
            .Should().ContainSingle("an apply that leaves the INI to the game ingests it as an ordinary file");

        await OwnInis(loadout, true);
        loadout = await Apply(loadout);

        var state = States(loadout).Should().ContainSingle().Subject;
        state.Baseline.Should().Be(UserIni.xxHash3());
        var written = await Live(IniStubbedGame.MainIni).ReadAllBytesAsync();
        IniDocument.Parse(written).Get("Archive", "bInvalidateOlderFiles").Should().Be("1", "the bake wins over the ingested copy at the same path");
        LoadoutIniState.LastBakedHash.Get(state).Should().Be(written.xxHash3());
    }

    [Fact]
    public async Task ABaselineThatLeftTheFileStoreRefusesToWriteInsteadOfWritingNothing()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            _ = new LoadoutIniState.New(tx)
            {
                LoadoutId = loadout.LoadoutId,
                Path = IniStubbedGame.MainIni.ToGamePathParentTuple(loadout.LoadoutId),
                Baseline = Hash.From(0xDEADBEEF),
            };
            await tx.Commit();
        }
        loadout = Refresh(loadout);

        var file = GameInis.IntrinsicFiles(Stub, loadout, ServiceProvider).Single();
        var act = async () => await file.Write(new MemoryStream(), loadout, new Dictionary<GamePath, SyncNode>());

        await act.Should().ThrowAsync<IniBaselineMissingException>();
    }
}

/// <summary>
/// A stubbed game with two INI files and a save redirect that goes through an INI key.
/// </summary>
public class IniStubbedGame : IGame, IGameData<IniStubbedGame>, IGameIniFiles, IPerLoadoutSaves
{
    public static GameId GameId { get; } = GameId.From("IniStubbedGame");
    public static string DisplayName => "Stubbed Game With INI Files";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(uint.MaxValue - 20);

    public static readonly GamePath MainIni = new(LocationId.Preferences, "Game.ini");
    public static readonly GamePath PrefsIni = new(LocationId.Preferences, "GamePrefs.ini");

    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [5001u],
        GOGProductIds = [5001L],
        EGSCatalogItemId = ["epic-game-id-ini-files"],
    };

    public IStreamFactory IconImage { get; } = new EmbeddedResourceStreamFactory<SilverModManager.StandardGameLocators.TestHelpers.StubbedGames.StubbedGame>("SilverModManager.StandardGameLocators.TestHelpers.Resources.question_mark_game.png");
    public IStreamFactory TileImage => throw new NotImplementedException("No game image for stubbed game.");

    private readonly IServiceProvider _serviceProvider;
    public IniStubbedGame(IServiceProvider provider) => _serviceProvider = provider;

    public GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, "");

    public ILoadoutSynchronizer Synchronizer => new IniSynchronizer(_serviceProvider, this);

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

    public IniSet IniFiles { get; } = new("Game.ini", "GamePrefs.ini");

    public GamePath IniPath(string fileName) => new(LocationId.Preferences, fileName);

    public GamePath? ManagedKeysFile => MainIni;

    public IReadOnlyList<IniKeyEdit> ManagedKeys(Loadout.ReadOnly loadout) =>
        [new IniKeyEdit("Archive", "bInvalidateOlderFiles", "1"), .. SaveRedirectIniKeys.For(this, loadout)];

    public SaveRedirectPlan? PlanSaveRedirect(Loadout.ReadOnly loadout) =>
        new SaveRedirectPlan.IniKey("General", "sLocalSavePath", $"Saves\\{LoadoutSaveSlot.IdOf(loadout)}\\");

    public SaveAdoptionPlan? PlanAdoptCurrentSave(Loadout.ReadOnly loadout, AbsolutePath saveRoot) => null;

    private sealed class IniSynchronizer(IServiceProvider provider, IniStubbedGame game) : DefaultSynchronizer(provider)
    {
        public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout) =>
            GameInis.IntrinsicFiles(game, loadout, provider).ToDictionary(file => file.Path);

        protected override void AdjustActions(Dictionary<GamePath, SyncNode> tree) => GameInis.LeaveDisownedInisOnDisk(game, tree);

        protected override ValueTask<Loadout.ReadOnly> PrepareLoadoutForSync(Loadout.ReadOnly loadout) => GameInis.Prepare(game, loadout, provider);
    }
}
