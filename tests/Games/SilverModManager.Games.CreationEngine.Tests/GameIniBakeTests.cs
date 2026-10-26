using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.Emitters;
using SilverModManager.Games.CreationEngine.Modlists;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.CreationEngine.Tests;

/// <summary>
/// A Fallout 4 loadout keeps its own copy of the game's INIs: the first sync takes the one on disk and writes it
/// back with the app's keys, and until then a notice says which files it will take.
/// </summary>
public class GameIniBakeTests(ITestOutputHelper helper) : AIsolatedGameTest<GameIniBakeTests, CreationEngine.Fallout4.Fallout4>(helper)
{
    private static readonly GamePath MainIni = new(LocationId.Preferences, "Fallout4.ini");
    private static readonly byte[] UserIni = "[Display]\r\niSize W=2560\r\n\r\n[General]\r\nsLanguage=EN\r\n"u8.ToArray();

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.Fallout4.Fallout4>(new Version("1.10.163"), locationsFileSystem: IsolatedKnownFolders.Create);

    private AbsolutePath Live => GameInstallation.Locations.ToAbsolutePath(MainIni);

    private async Task WriteUserIni()
    {
        Live.Parent.CreateDirectory();
        await Live.WriteAllBytesAsync(UserIni);
    }

    private async Task<List<Diagnostic>> Notices(Loadout.ReadOnly loadout)
    {
        var found = new List<Diagnostic>();
        await foreach (var diagnostic in new GameInisTakenOverEmitter((IGameIniFiles)Game).Diagnose(Refresh(loadout), CancellationToken.None))
            found.Add(diagnostic);
        return found;
    }

    [Fact]
    public async Task TheSyncTakesTheIniOnDiskAndWritesItBackWithTheArchiveKey()
    {
        var loadout = await CreateLoadout();
        await WriteUserIni();

        await Synchronizer.Synchronize(Refresh(loadout));

        var state = LoadoutIniState.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().ContainSingle().Subject;
        state.Baseline.Should().Be(UserIni.xxHash3());
        var baked = IniDocument.Parse(await Live.ReadAllBytesAsync());
        baked.Get("Archive", "bInvalidateOlderFiles").Should().Be("1");
        baked.Get("Display", "iSize W").Should().Be("2560");
        LoadoutIniState.LastBakedHash.Get(state).Should().Be((await Live.ReadAllBytesAsync()).xxHash3());
    }

    [Fact]
    public async Task OneNoticeNamesTheFilesTheNextApplyTakesOver()
    {
        var loadout = await CreateLoadout();
        await WriteUserIni();

        var notice = (await Notices(loadout)).Should().ContainSingle().Subject;
        notice.FormatSummary(new PlainDiagnosticWriter()).Should().Contain("Fallout4.ini").And.NotContain("Fallout4Prefs.ini", "only files on disk are taken over");

        await Synchronizer.Synchronize(Refresh(loadout));

        (await Notices(loadout)).Should().BeEmpty("the loadout has its own copy now");
    }

    [Fact]
    public async Task NoNoticeWhenTheLoadoutLeavesTheInisToTheGame()
    {
        var loadout = await CreateLoadout();
        await WriteUserIni();
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(loadout.Id, Loadout.OwnsGameInis, false);
            await tx.Commit();
        }

        (await Notices(loadout)).Should().BeEmpty();
    }

    [Fact]
    public void TheAppsKeysGoInTheMainIniNotThePrefsFile()
    {
        ((IGameIniFiles)Game).ManagedKeysFile.Should().Be(MainIni);
    }

    [Theory]
    [InlineData("Skyrim SE", new[] { "Skyrim.ini", "SkyrimPrefs.ini", "SkyrimCustom.ini" })]
    [InlineData("Fallout 4", new[] { "Fallout4.ini", "Fallout4Prefs.ini", "Fallout4Custom.ini" })]
    public void AGameOwnsTheIniFilesItsProfileHolds(string title, string[] expected)
    {
        var game = title == "Skyrim SE" ? CreationEngine.SkyrimSE.SkyrimSE.GameId : CreationEngine.Fallout4.Fallout4.GameId;

        CreationEngineMo2Target.OwnedIniFiles(game).Select(p => p.FileName.ToString()).Should().Equal(expected);
    }

    [Fact]
    public void ExactlyTheGamesWithInisToOwnOfferTheChoice()
    {
        var games = typeof(ACreationEngineGame<>).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && type.BaseType is { IsGenericType: true } baseType && baseType.GetGenericTypeDefinition() == typeof(ACreationEngineGame<>))
            .ToList();
        games.Should().Contain(typeof(CreationEngine.Fallout4.Fallout4)).And.Contain(typeof(CreationEngine.Morrowind.Morrowind));

        foreach (var type in games)
        {
            var gameId = type.GetProperty("GameId", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            gameId.Should().BeOfType<GameId>(type.Name);

            typeof(IHasIniFiles).IsAssignableFrom(type).Should().Be(
                CreationEngineMo2Target.OwnedIniFiles((GameId)gameId).Count > 0,
                "{0} shows the \"Own the game INIs\" choice only when it has INIs to own", type.Name);
        }
    }

    [Fact]
    public void MorrowindsIniIsOwnedLikeTheOthersWithItsPluginListRenderedIntoIt()
    {
        CreationEngineMo2Target.OwnedIniFiles(CreationEngine.Morrowind.Morrowind.GameId).Should().Equal(new GamePath(LocationId.Game, "Morrowind.ini"));
    }

    [Fact]
    public void ARemasteredGameHasNoIniToOwn()
    {
        CreationEngineMo2Target.OwnedIniFiles(CreationEngine.OblivionRemastered.OblivionRemastered.GameId).Should().BeEmpty();
    }
}
