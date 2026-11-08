using System.Text;
using SilverModManager.Abstractions.Loadouts;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// Which tweak file is the user's: only a top-level <c>Loadout [X.ini].ini</c> in the loadout's own game settings
/// group. A file of that name inside a modlist's group is the modlist's layer.
/// </summary>
public class IniTweakOwnershipTests(ITestOutputHelper helper) : IniTweakTestBase<IniTweakOwnershipTests>(helper)
{
    private const string TweakName = "Loadout [Game.ini].ini";
    private const string List = "Yggdrasil VR";
    private static readonly byte[] UserIni = "[Display]\r\niSize W=2560\r\niSize H=1440\r\n"u8.ToArray();

    private async Task<Loadout.ReadOnly> LoadoutWithBaseline()
    {
        var loadout = await CreateLoadout();
        await IniEdits.SetBaseline(Connection, FileStore, loadout, IniStubbedGame.MainIni, UserIni);
        return Refresh(loadout);
    }

    // A modlist group whose own tweak files are for another INI, so only the nested file matters for Game.ini.
    private async Task<LoadoutItemGroupId> AddList(Loadout.ReadOnly loadout) =>
        await AddTweakMod(loadout, List, "Filler [Other.ini].ini", "[A]\r\nb=1\r\n", priority: 20, collection: true);

    private async Task<LoadoutItemGroupId> AddListSettings(Loadout.ReadOnly loadout, LoadoutItemGroupId list, string content, ulong priority = 5) =>
        await AddTweakMod(loadout, "Game settings", TweakName, content, priority, parent: list, settingsGroup: true);

    private IniTweakSource[] Sources(Loadout.ReadOnly loadout) => IniTweakLayers.All(Refresh(loadout), IniStubbedGame.MainIni).ToArray();

    private async Task<string> Rendered(Loadout.ReadOnly loadout) =>
        IniDocument.Parse(await GameInis.Render(Stub, FileStore, Refresh(loadout), IniStubbedGame.MainIni)).ToText();

    private static FileIdentity IdentityOf(string text) => FileIdentity.XxHash3(Encoding.UTF8.GetBytes(text).xxHash3());

    private static bool IsInCollection(LoadoutItem.ReadOnly item) =>
        item.TryGetAsLoadoutItemGroup(out var group) && group.TryGetAsCollectionGroup(out _);

    [Fact]
    public async Task ALoadoutNamedTweakInsideAModlistsSettingsGroupIsTheModlistsLayerNotTheLoadouts()
    {
        var loadout = await LoadoutWithBaseline();
        var list = await AddList(loadout);
        await AddListSettings(loadout, list, "[Papyrus]\r\nbEnableLogging=1\r\n");

        var source = Sources(loadout).Should().ContainSingle().Subject;

        source.Name.Should().Be("Loadout");
        source.IsLoadoutTweak.Should().BeFalse();
        source.Kind.Should().Be(IniTweakKind.List);
        source.SourceName.Should().Be(List);
        IniTweakLayers.UserTweak(Refresh(loadout), IniStubbedGame.MainIni).Should().BeNull();
    }

    [Fact]
    public async Task AModInsideACollectionKeepsItsOwnNameAsTheSource()
    {
        var loadout = await LoadoutWithBaseline();
        var collection = await AddList(loadout);
        await AddTweakMod(loadout, "Brighter Nights", "Brighter [Game.ini].ini", "[Display]\r\nfGamma=2\r\n", priority: 6, parent: collection);

        var source = Sources(loadout).Should().ContainSingle().Subject;

        source.SourceName.Should().Be("Brighter Nights");
        source.Kind.Should().Be(IniTweakKind.List);
    }

    [Fact]
    public async Task SavingTheLoadoutTweakWhileAModlistHasALoadoutNamedFileCreatesTheUsersGroupAndLeavesTheModlistsFileAlone()
    {
        const string listText = "[Display]\r\niSize H=1\r\n";
        var loadout = await LoadoutWithBaseline();
        var list = await AddList(loadout);
        await AddListSettings(loadout, list, listText);

        await IniEdits.SetLoadoutTweak(Connection, FileStore, Refresh(loadout), IniStubbedGame.MainIni, "[Display]\r\niSize H=900\r\n"u8.ToArray());

        var fresh = Refresh(loadout);
        var nested = Sources(loadout).Single(s => s.Kind == IniTweakKind.List);
        nested.Identity.Should().Be(IdentityOf(listText), "the modlist's file is not the user's to rewrite");
        LoadoutItem.FindByLoadout(Connection.Db, fresh.LoadoutId).OfTypeLoadoutItemGroup()
            .Where(g => g.IsGameSettingsGroup && !g.AsLoadoutItem().HasParent()).Should().ContainSingle();
        var user = IniTweakLayers.UserTweak(fresh, IniStubbedGame.MainIni)!.Value;
        user.FileId.Should().NotBe(nested.FileId);
        user.Kind.Should().Be(IniTweakKind.User);
    }

    [Fact]
    public async Task TheUsersTweakIsMergedAfterAModlistsLoadoutNamedTweak()
    {
        var loadout = await LoadoutWithBaseline();
        await AddTweakMod(loadout, "Game settings", TweakName, "[Display]\r\niSize H=900\r\n", priority: 1, settingsGroup: true);
        var list = await AddList(loadout);
        await AddListSettings(loadout, list, "[Display]\r\niSize H=1\r\n", priority: 5);

        var text = await Rendered(loadout);

        IniDocument.Parse(Encoding.UTF8.GetBytes(text)).Get("Display", "iSize H").Should().Be("900");
    }

    [Fact]
    public async Task AListModsTweakBeatsTheListsSettingsGroupTweak()
    {
        var loadout = await LoadoutWithBaseline();
        var list = await AddList(loadout);
        await AddListSettings(loadout, list, "[Display]\r\niSize H=1\r\niSize W=3\r\n", priority: 5);
        await AddTweakMod(loadout, "A list mod", "Mod [Game.ini].ini", "[Display]\r\niSize H=700\r\n", priority: 8, parent: list);

        var rendered = IniDocument.Parse(Encoding.UTF8.GetBytes(await Rendered(loadout)));

        rendered.Get("Display", "iSize H").Should().Be("700", "the list's settings group sits below the list's mods");
        rendered.Get("Display", "iSize W").Should().Be("3");
    }

    [Fact]
    public async Task ANestedLoadoutNamedTweakAsTheOnlyLayerRendersTheSameBytesAsBefore()
    {
        const string text = "[Display]\r\niSize H=900\r\n[Papyrus]\r\nbEnableLogging=1\r\n";
        var asList = await LoadoutWithBaseline();
        var list = await AddList(asList);
        await AddListSettings(asList, list, text);
        var asUser = await LoadoutWithBaseline();
        await AddTweakMod(asUser, "Game settings", TweakName, text, priority: 5, settingsGroup: true);

        Sources(asList).Single().Kind.Should().Be(IniTweakKind.List);
        Sources(asUser).Single().Kind.Should().Be(IniTweakKind.User);
        (await Rendered(asList)).Should().Be(await Rendered(asUser));
    }

    [Fact]
    public async Task AGameChangeToAKeyInTheLegacyLayerIsStillOverriddenAndNamesTheList()
    {
        var loadout = await LoadoutWithBaseline();
        var list = await AddList(loadout);
        await AddListSettings(loadout, list, "[Display]\r\niSize H=900\r\n");
        var written = await GameInis.Render(Stub, FileStore, Refresh(loadout), IniStubbedGame.MainIni);
        var live = IniDocument.Parse(written);
        live.Set("Display", "iSize H", "1000");

        var changes = await GameInis.ChangesByGame(Stub, FileStore, Refresh(loadout), IniStubbedGame.MainIni, live.ToBytes(), written);

        var change = changes.Should().ContainSingle().Subject;
        change.Kind.Should().Be(GameIniChangeKind.TweakOverrides);
        change.Owner.Should().Be($"Loadout ({List})");
    }

    [Fact]
    public async Task UserTweakReturnsTheRowThatMergesLast()
    {
        var loadout = await LoadoutWithBaseline();
        await AddTweakMod(loadout, "Game settings", TweakName, "[A]\r\nwins=0\r\n", priority: 1, settingsGroup: true);
        await AddTweakMod(loadout, "Game settings 2", TweakName, "[A]\r\nwins=1\r\n", priority: 2, settingsGroup: true);

        var user = IniTweakLayers.UserTweak(Refresh(loadout), IniStubbedGame.MainIni);

        user.Should().NotBeNull();
        user!.Value.Identity.Should().Be(IdentityOf("[A]\r\nwins=1\r\n"));
    }

    [Fact]
    public async Task TheUsersLoadoutTweakIsWrittenOutsideTheListsSettingsGroup()
    {
        var loadout = await LoadoutWithBaseline();
        var list = await AddList(loadout);
        await AddListSettings(loadout, list, "[Display]\r\niSize H=1\r\n");

        await IniEdits.SetLoadoutTweak(Connection, FileStore, Refresh(loadout), IniStubbedGame.MainIni, "[Display]\r\niSize H=900\r\n"u8.ToArray());

        var user = IniTweakLayers.UserTweak(Refresh(loadout), IniStubbedGame.MainIni)!.Value;
        var item = LoadoutItem.Load(Connection.Db, user.FileId);
        item.GetThisAndParents().Any(IsInCollection).Should().BeFalse();
    }
}
