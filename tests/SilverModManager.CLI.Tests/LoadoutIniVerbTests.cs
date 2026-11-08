using System.Text.Json.Nodes;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.App.Mcp;
using SilverModManager.Sdk.Settings;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;
using Microsoft.Extensions.DependencyInjection;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>loadout ini list</c> and <c>loadout ini show</c> read a loadout's copies of the game's INI files, the tweak
/// layers laid over them and the bytes Apply would write; <c>set</c>, <c>unset</c> and <c>replace</c> change the
/// loadout's own tweak.
/// </summary>
[Collection("ConsoleOutput")]
public class LoadoutIniVerbTests(ITestOutputHelper helper) : SkyrimIniVerbTest<LoadoutIniVerbTests>(helper)
{
    private const string SkyrimText = "[General]\r\nsLanguage=ENGLISH\r\n[Papyrus]\r\nbEnableLogging=0\r\n";

    private async Task<JsonObject> ListJson(Loadout.ReadOnly loadout)
    {
        var (exit, output) = await Run("loadout", "ini", "list", "-l", loadout.LoadoutId.ToString(), "--json");
        exit.Should().Be(0, output);
        return JsonNode.Parse(output)!.AsObject();
    }

    [Fact]
    public async Task ListPrintsEveryIniWithItsLayersKindsAndWhetherTheLoadoutOwnsThem()
    {
        var loadout = await CreateLoadout();
        await SeedBaseline(loadout, "Skyrim.ini", SkyrimText);
        await AddTweakLayer(loadout, "A Mod", "Mod tweak [Skyrim.ini].ini", "[Papyrus]\r\nbEnableLogging=1\r\n", priority: 10);
        await AddTweakLayer(loadout, "A List", "List tweak [Skyrim.ini].ini", "[General]\r\nsLanguage=FRENCH\r\n", priority: 5, collection: true);
        await AddTweakLayer(loadout, "Game settings", "Loadout [Skyrim.ini].ini", "[General]\r\nsLanguage=GERMAN\r\n", priority: 1, settingsGroup: true);

        var json = await ListJson(loadout);

        ((bool)json["ownsGameInis"]!).Should().BeTrue();
        var files = json["files"]!.AsArray().Select(f => f!.AsObject()).ToArray();
        files.Select(f => (string)f["file"]!).Should().Equal("Skyrim.ini", "SkyrimPrefs.ini", "SkyrimCustom.ini");
        files.Select(f => (bool)f["hasBaseline"]!).Should().Equal(true, false, false);
        var layers = files[0]["layers"]!.AsArray().Select(l => l!.AsObject()).ToArray();
        layers.Select(l => (string)l["kind"]!).Should().BeEquivalentTo("mod", "list", "user");
        layers.Single(l => (string)l["kind"]! == "list")["source"]!.GetValue<string>().Should().Be("A List");
        layers.Should().OnlyContain(l => (bool)l["on"]! && (bool)l["active"]!);
        files[1]["layers"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task ListReportsAnUnknownPathWhenTheLibraryIsNotMounted()
    {
        var loadout = await CreateLoadout();
        await SeedBaseline(loadout, "Skyrim.ini", SkyrimText);
        LibraryMounted = false;

        var json = await ListJson(loadout);
        var (exit, output) = await Run("loadout", "ini", "list", "-l", loadout.LoadoutId.ToString());

        json["files"]!.AsArray().Select(f => f!["path"]).Should().OnlyContain(p => p == null);
        exit.Should().Be(0, output);
        output.Should().Contain("Skyrim.ini  -> unknown  baseline: yes");
    }

    [Fact]
    public async Task ListComparesTheBakeWithTheLoadoutAndTheFileOnDisk()
    {
        var loadout = await CreateLoadout();
        await SeedBaseline(loadout, "Skyrim.ini", SkyrimText);
        var path = GameInstallation.Locations.ToAbsolutePath(IniPath("Skyrim.ini"));
        var (_, noBake) = await Run("loadout", "ini", "list", "-l", loadout.LoadoutId.ToString());
        var neverApplied = await ListJson(loadout);

        var rendered = await GameInis.Render((IGameIniFiles)GameInstallation.Game, Store, loadout.Rebase(), IniPath("Skyrim.ini"));
        path.Parent.CreateDirectory();
        await path.WriteAllBytesAsync(rendered);
        await SetLastBake(loadout, IniPath("Skyrim.ini"), rendered);
        var applied = await ListJson(loadout);

        await path.WriteAllBytesAsync(Encoding.UTF8.GetBytes("[General]\r\nsLanguage=GERMAN\r\n"));
        var edited = await ListJson(loadout);

        path.Delete();
        var gone = await ListJson(loadout);

        await AddTweakLayer(loadout, "A Mod", "Mod tweak [Skyrim.ini].ini", "[Papyrus]\r\nbEnableLogging=1\r\n", priority: 10);
        var layered = await ListJson(loadout);

        neverApplied["files"]![0]!["upToDate"].Should().BeNull("no Apply has recorded a bake to compare with");
        neverApplied["files"]![0]!["onDisk"].Should().BeNull();
        noBake.Should().Contain("changed since the last Apply: unknown (never applied)").And.Contain("on disk: never applied");
        applied["files"]![0]!["upToDate"]!.GetValue<bool>().Should().BeTrue();
        applied["files"]![0]!["onDisk"]!.GetValue<bool>().Should().BeTrue();
        edited["files"]![0]!["onDisk"]!.GetValue<bool>().Should().BeFalse();
        gone["files"]![0]!["onDisk"].Should().BeNull();
        layered["files"]![0]!["upToDate"]!.GetValue<bool>().Should().BeFalse("a layer was added after the bake");
    }

    [Fact]
    public async Task ListSaysWhenTheLoadoutDoesNotOwnItsInis()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(loadout.Id, Loadout.OwnsGameInis, false);
            await tx.Commit();
        }

        var (exit, output) = await Run("loadout", "ini", "list", "-l", loadout.LoadoutId.ToString());

        exit.Should().Be(0, output);
        output.Should().Contain("Owns the game's INIs: no");
    }

    [Fact]
    public async Task ShowPrintsALayerByItsIdAndRefusesAnUnknownOne()
    {
        var loadout = await CreateLoadout();
        await SeedBaseline(loadout, "Skyrim.ini", SkyrimText);
        await AddTweakLayer(loadout, "A Mod", "Mod tweak [Skyrim.ini].ini", "[Papyrus]\r\nbEnableLogging=1\r\n", priority: 10);
        var json = await ListJson(loadout);
        var id = (string)json["files"]![0]!["layers"]![0]!["fileId"]!;

        var (exit, output) = await Run("loadout", "ini", "show", "-l", loadout.LoadoutId.ToString(), "-f", "Skyrim.ini", "--layer", id);
        var (badExit, badOutput) = await Run("loadout", "ini", "show", "-l", loadout.LoadoutId.ToString(), "-f", "Skyrim.ini", "--layer", "ffff");

        exit.Should().Be(0, output);
        output.Should().Contain("bEnableLogging=1");
        badExit.Should().NotBe(0);
        badOutput.Should().Contain("ffff is not a tweak layer of Skyrim.ini. 'loadout ini list' prints the ids.");
    }

    [Fact]
    public async Task ShowPrintsTheResultTheBaselineAndTheUsersTweak()
    {
        var loadout = await CreateLoadout();
        await SeedBaseline(loadout, "Skyrim.ini", SkyrimText);
        await AddTweakLayer(loadout, "A Mod", "Mod tweak [Skyrim.ini].ini", "[Papyrus]\r\nbEnableLogging=1\r\n", priority: 10);
        var id = loadout.LoadoutId.ToString();

        var (_, result) = await Run("loadout", "ini", "show", "-l", id, "-f", "Skyrim.ini");
        var (_, baseline) = await Run("loadout", "ini", "show", "-l", id, "-f", "skyrim.ini", "--part", "baseline");
        var (tweakExit, tweak) = await Run("loadout", "ini", "show", "-l", id, "-f", "Skyrim.ini", "--part", "tweak");

        result.Should().Contain("bEnableLogging=1").And.Contain("sLanguage=ENGLISH");
        baseline.Should().Contain("bEnableLogging=0");
        tweakExit.Should().Be(0, tweak);
        tweak.Should().BeEmpty("the loadout has no tweak of its own yet");
    }

    [Fact]
    public async Task ShowHandsTheConsoleTheTextAsTheTemplateBecauseAnArgumentIsFlattenedToOneLine()
    {
        var loadout = await CreateLoadout();
        await SeedBaseline(loadout, "Skyrim.ini", SkyrimText);
        var renderer = new LoggingRenderer();

        var exit = await ServiceProvider.GetRequiredService<CommandLineConfigurator>()
            .RunAsync(["loadout", "ini", "show", "-l", loadout.LoadoutId.ToString(), "-f", "Skyrim.ini", "--part", "baseline"], renderer, CancellationToken.None);

        exit.Should().Be(0);
        var text = renderer.Logs.OfType<Text>().Should().ContainSingle().Subject;
        text.Arguments.Should().BeEmpty("the console turns line breaks inside an argument into spaces");
        text.Template.Should().Contain("[General]\r\nsLanguage=ENGLISH\r\n");
    }

    [Fact]
    public async Task ShowPrintsTheUsersOwnTweakText()
    {
        var loadout = await CreateLoadout();
        await SeedBaseline(loadout, "Skyrim.ini", SkyrimText);
        await AddTweakLayer(loadout, "A Mod", "Mod tweak [Skyrim.ini].ini", "[Papyrus]\r\nbEnableLogging=1\r\n", priority: 10);
        await AddTweakLayer(loadout, "Game settings", "Loadout [Skyrim.ini].ini", "[General]\r\nsLanguage=GERMAN\r\n", priority: 1, settingsGroup: true);

        var (exit, output) = await Run("loadout", "ini", "show", "-l", loadout.LoadoutId.ToString(), "-f", "Skyrim.ini", "--part", "tweak");

        exit.Should().Be(0, output);
        output.Should().Contain("sLanguage=GERMAN").And.NotContain("bEnableLogging");
    }

    [Fact]
    public async Task ALoadoutNamedFileInsideAListIsTheListsLayerAndNotTheUsersTweak()
    {
        var loadout = await CreateLoadout();
        await SeedBaseline(loadout, "Skyrim.ini", SkyrimText);
        var list = await AddTweakLayer(loadout, "A List", "Filler [Other.ini].ini", "[A]\r\nb=1\r\n", priority: 20, collection: true);
        await AddTweakLayer(loadout, "Game settings", "Loadout [Skyrim.ini].ini", "[General]\r\nsLanguage=FRENCH\r\n", priority: 5, parent: list, settingsGroup: true);
        var id = loadout.LoadoutId.ToString();

        var json = await ListJson(loadout);
        var (exit, tweak) = await Run("loadout", "ini", "show", "-l", id, "-f", "Skyrim.ini", "--part", "tweak");

        var layer = json["files"]!.AsArray()[0]!["layers"]!.AsArray().Single()!.AsObject();
        ((string)layer["kind"]!).Should().Be("list");
        ((string)layer["source"]!).Should().Be("A List");
        exit.Should().Be(0, tweak);
        tweak.Should().BeEmpty("the list's file is not the loadout's own tweak");
    }

    [Fact]
    public async Task ShowWarnsThatNothingIsBakedWhenTheLoadoutDoesNotOwnItsInis()
    {
        var loadout = await CreateLoadout();
        await SeedBaseline(loadout, "Skyrim.ini", SkyrimText);
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(loadout.Id, Loadout.OwnsGameInis, false);
            await tx.Commit();
        }
        var id = loadout.LoadoutId.ToString();

        var (exit, result) = await Run("loadout", "ini", "show", "-l", id, "-f", "Skyrim.ini");
        var (_, baseline) = await Run("loadout", "ini", "show", "-l", id, "-f", "Skyrim.ini", "--part", "baseline");

        exit.Should().Be(0, result);
        result.Should().Contain("sLanguage=ENGLISH").And.Contain("This loadout does not own its INIs, so nothing is baked until that is turned on in the loadout card.");
        baseline.Should().NotContain("This loadout does not own its INIs, so nothing is baked until that is turned on in the loadout card.");
    }

    [Fact]
    public async Task ShowRefusesAnUnknownPart()
    {
        var loadout = await CreateLoadout();
        await SeedBaseline(loadout, "Skyrim.ini", SkyrimText);

        var (exit, output) = await Run("loadout", "ini", "show", "-l", loadout.LoadoutId.ToString(), "-f", "Skyrim.ini", "--part", "xyz");

        exit.Should().NotBe(0);
        output.Should().Contain("xyz is not a part. Use result, baseline or tweak.");
    }

    [Fact]
    public async Task ShowRefusesAPartTogetherWithALayer()
    {
        var loadout = await CreateLoadout();
        await SeedBaseline(loadout, "Skyrim.ini", SkyrimText);

        var (exit, output) = await Run("loadout", "ini", "show", "-l", loadout.LoadoutId.ToString(), "-f", "Skyrim.ini", "--part", "result", "--layer", "1");

        exit.Should().NotBe(0);
        output.Should().Contain("Give either --part or --layer.");
    }

    [Fact]
    public async Task ShowRefusesAnIniWithoutABaseline()
    {
        var loadout = await CreateLoadout();

        var (exit, output) = await Run("loadout", "ini", "show", "-l", loadout.LoadoutId.ToString(), "-f", "Skyrim.ini");

        exit.Should().NotBe(0);
        output.Should().Contain("Skyrim.ini has no copy in this loadout yet. The first Apply takes one from the game folder.");
    }

    [Fact]
    public async Task ShowRefusesAFileTheGameDoesNotKeepAndNamesTheGamesFiles()
    {
        var loadout = await CreateLoadout();

        var (exit, output) = await Run("loadout", "ini", "show", "-l", loadout.LoadoutId.ToString(), "-f", "Nope.ini");

        exit.Should().NotBe(0);
        output.Should().Contain("Nope.ini is not one of").And.Contain("Skyrim.ini, SkyrimPrefs.ini, SkyrimCustom.ini");
    }

    private async Task<(IniDocument Document, byte[] Bytes)?> UserTweakOf(Loadout.ReadOnly loadout, string fileName)
    {
        var fresh = Loadout.Load(Connection.Db, loadout.LoadoutId);
        if (IniTweakLayers.UserTweak(fresh, IniPath(fileName)) is not { } user) return null;
        var bytes = await Store.Load(user.Identity);
        return (IniDocument.Parse(bytes), bytes);
    }

    // Refusals and notes wrap names in code spans; the sentences are compared without them.
    private static string Plain(string output) => output.Replace("`", "");

    private const string RestoreTail = "Run 'loadout synchronize' to write it to the game folder. 'loadout revisions' lists the restore point; 'loadout revert' undoes it until the next Apply.";

    [Fact]
    public async Task SetWritesTheKeyIntoTheUsersTweakAndARestorePoint()
    {
        var loadout = await CreateLoadout();
        await SeedBaseline(loadout, "Skyrim.ini", SkyrimText);
        var before = LatestRevisionNumber(loadout.LoadoutId);

        var (exit, output) = await Run("loadout", "ini", "set", "-l", loadout.LoadoutId.ToString(), "-f", "Skyrim.ini", "-s", "Papyrus", "-k", "bEnableTrace", "-v", "1");
        var (secondExit, second) = await Run("loadout", "ini", "set", "-l", loadout.LoadoutId.ToString(), "-f", "skyrim.ini", "-s", "Papyrus", "-k", "bEnableTrace", "-v=-1");

        exit.Should().Be(0, output);
        Plain(output).Should().Contain("Set [Papyrus] bEnableTrace=1 in the loadout tweak of Skyrim.ini (was not set).").And.Contain(RestoreTail);
        secondExit.Should().Be(0, second);
        Plain(second).Should().Contain("(was 1)");
        LatestRevisionNumber(loadout.LoadoutId).Should().BeGreaterThan(before);
        var tweak = await UserTweakOf(loadout, "Skyrim.ini");
        tweak!.Value.Document.Get("Papyrus", "bEnableTrace").Should().Be("-1");
        var (_, result) = await Run("loadout", "ini", "show", "-l", loadout.LoadoutId.ToString(), "-f", "Skyrim.ini");
        result.Should().Contain("bEnableTrace=-1");
    }

    [Fact]
    public async Task SetThenRevertBringsTheOldTweakBack()
    {
        var loadout = await CreateLoadout();
        await SeedBaseline(loadout, "Skyrim.ini", SkyrimText);
        var id = loadout.LoadoutId.ToString();
        await Run("loadout", "ini", "set", "-l", id, "-f", "Skyrim.ini", "-s", "Papyrus", "-k", "bEnableTrace", "-v", "1");
        await Run("loadout", "ini", "set", "-l", id, "-f", "Skyrim.ini", "-s", "Papyrus", "-k", "bEnableTrace", "-v", "0");
        (await UserTweakOf(loadout, "Skyrim.ini"))!.Value.Document.Get("Papyrus", "bEnableTrace").Should().Be("0");

        var (exit, output) = await Run("loadout", "revert", "-l", id, "-r", LatestRevisionNumber(loadout.LoadoutId).ToString());

        exit.Should().Be(0, output);
        (await UserTweakOf(loadout, "Skyrim.ini"))!.Value.Document.Get("Papyrus", "bEnableTrace").Should().Be("1", "the restore point was taken before the write");
    }

    [Fact]
    public async Task ReplaceRefusesAFromThatIsNotAFile()
    {
        var loadout = await CreateLoadout();
        await SeedBaseline(loadout, "Skyrim.ini", SkyrimText);
        var before = LatestRevisionNumber(loadout.LoadoutId);

        var (exit, output) = await Run("loadout", "ini", "replace", "-l", loadout.LoadoutId.ToString(), "-f", "Skyrim.ini", "--from", "/nonexistent/tweak.ini");

        exit.Should().NotBe(0);
        Plain(output).Should().Contain("/nonexistent/tweak.ini is not a file.");
        LatestRevisionNumber(loadout.LoadoutId).Should().Be(before);
    }

    [Fact]
    public async Task SettingTheSameValueAgainWritesNoRestorePoint()
    {
        var loadout = await CreateLoadout();
        await SeedBaseline(loadout, "Skyrim.ini", SkyrimText);
        var id = loadout.LoadoutId.ToString();
        await Run("loadout", "ini", "set", "-l", id, "-f", "Skyrim.ini", "-s", "Papyrus", "-k", "bEnableLogging", "-v", "1");
        var revisions = LatestRevisionNumber(loadout.LoadoutId);
        var bytes = (await UserTweakOf(loadout, "Skyrim.ini"))!.Value.Bytes;

        var (exit, output) = await Run("loadout", "ini", "set", "-l", id, "-f", "Skyrim.ini", "-s", "Papyrus", "-k", "bEnableLogging", "-v", "1");

        exit.Should().Be(0, output);
        Plain(output).Should().Contain("[Papyrus] bEnableLogging is already 1 in the loadout tweak of Skyrim.ini.");
        LatestRevisionNumber(loadout.LoadoutId).Should().Be(revisions);
        (await UserTweakOf(loadout, "Skyrim.ini"))!.Value.Bytes.Should().Equal(bytes);
    }

    [Fact]
    public async Task SetRefusesAnUnknownIniAndNamesTheGamesFiles()
    {
        var loadout = await CreateLoadout();
        var before = LatestRevisionNumber(loadout.LoadoutId);

        var (exit, output) = await Run("loadout", "ini", "set", "-l", loadout.LoadoutId.ToString(), "-f", "Nope.ini", "-s", "A", "-k", "b", "-v", "1");

        exit.Should().NotBe(0);
        output.Should().Contain("Nope.ini is not one of").And.Contain("Skyrim.ini, SkyrimPrefs.ini, SkyrimCustom.ini");
        LatestRevisionNumber(loadout.LoadoutId).Should().Be(before);
    }

    [Fact]
    public async Task SetRefusesAKeyWithAnEqualsSignAndWritesNoRestorePoint()
    {
        var loadout = await CreateLoadout();
        await SeedBaseline(loadout, "Skyrim.ini", SkyrimText);
        var before = LatestRevisionNumber(loadout.LoadoutId);

        var (exit, output) = await Run("loadout", "ini", "set", "-l", loadout.LoadoutId.ToString(), "-f", "Skyrim.ini", "-s", "Papyrus", "-k", "a=b", "-v", "1");

        exit.Should().NotBe(0);
        Plain(output).Should().Contain("a=b is not a key name: it cannot be empty or contain '=' or a line break.");
        LatestRevisionNumber(loadout.LoadoutId).Should().Be(before);
        (await UserTweakOf(loadout, "Skyrim.ini")).Should().BeNull();
    }

    [Fact]
    public async Task SetRefusesAKeyTheAppManages()
    {
        var loadout = await CreateLoadout();
        await SeedBaseline(loadout, "Skyrim.ini", SkyrimText);
        var before = LatestRevisionNumber(loadout.LoadoutId);

        var (exit, output) = await Run("loadout", "ini", "set", "-l", loadout.LoadoutId.ToString(), "-f", "Skyrim.ini", "-s", "Archive", "-k", "bInvalidateOlderFiles", "-v", "0");

        exit.Should().NotBe(0);
        Plain(output).Should().Contain("sets [Archive] bInvalidateOlderFiles in Skyrim.ini itself (1 on every Apply), so a tweak of it would have no effect.");
        LatestRevisionNumber(loadout.LoadoutId).Should().Be(before);
        (await UserTweakOf(loadout, "Skyrim.ini")).Should().BeNull();
    }

    [Fact]
    public async Task SetWarnsWhenTheLoadoutDoesNotOwnItsInis()
    {
        var loadout = await CreateLoadout();
        await SeedBaseline(loadout, "Skyrim.ini", SkyrimText);
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(loadout.Id, Loadout.OwnsGameInis, false);
            await tx.Commit();
        }

        var (exit, output) = await Run("loadout", "ini", "set", "-l", loadout.LoadoutId.ToString(), "-f", "Skyrim.ini", "-s", "Papyrus", "-k", "bEnableTrace", "-v", "1");

        exit.Should().Be(0, output);
        output.Split('\n').Select(l => l.TrimEnd('\r')).Should().Contain("This loadout does not own its INIs, so nothing is baked until that is turned on in the loadout card.", "the warning is its own line");
        (await UserTweakOf(loadout, "Skyrim.ini"))!.Value.Document.Get("Papyrus", "bEnableTrace").Should().Be("1", "the tweak is stored anyway");
    }

    [Fact]
    public async Task UnsetRemovesTheKeyAndSaysSoWhenItWasNotThere()
    {
        var loadout = await CreateLoadout();
        await SeedBaseline(loadout, "Skyrim.ini", SkyrimText);
        var id = loadout.LoadoutId.ToString();
        var path = IniPath("Skyrim.ini");
        await IniEdits.SetLoadoutTweak(Connection, Store, loadout, path, Encoding.UTF8.GetBytes("[Papyrus]\r\nbEnableTrace=1\r\nbEnableTrace=1\r\nbEnableLogging=1\r\n"), null);
        var before = LatestRevisionNumber(loadout.LoadoutId);

        var (exit, output) = await Run("loadout", "ini", "unset", "-l", id, "-f", "Skyrim.ini", "-s", "Papyrus", "-k", "bEnableTrace");
        var afterRemoval = LatestRevisionNumber(loadout.LoadoutId);
        var (missingExit, missing) = await Run("loadout", "ini", "unset", "-l", id, "-f", "Skyrim.ini", "-s", "Papyrus", "-k", "bEnableTrace");

        exit.Should().Be(0, output);
        Plain(output).Should().Contain("Removed [Papyrus] bEnableTrace from the loadout tweak of Skyrim.ini.").And.Contain(RestoreTail);
        afterRemoval.Should().BeGreaterThan(before);
        (await UserTweakOf(loadout, "Skyrim.ini"))!.Value.Document.ToText().Should().Be("[Papyrus]\r\nbEnableLogging=1\r\n");
        missingExit.Should().Be(0, missing);
        Plain(missing).Should().Contain("The loadout tweak of Skyrim.ini does not set [Papyrus] bEnableTrace.");
        LatestRevisionNumber(loadout.LoadoutId).Should().Be(afterRemoval);
    }

    [Fact]
    public async Task ReplaceKeepsTheTweaksEncodingAndAnEmptyTextClearsIt()
    {
        var loadout = await CreateLoadout();
        await SeedBaseline(loadout, "Skyrim.ini", SkyrimText);
        var id = loadout.LoadoutId.ToString();
        var windows1252 = Encoding.GetEncoding(1252);
        await IniEdits.SetLoadoutTweak(Connection, Store, loadout, IniPath("Skyrim.ini"), windows1252.GetBytes("[General]\r\nsLanguage=caf\u00e9\r\n"), null);
        var before = LatestRevisionNumber(loadout.LoadoutId);

        var (exit, output) = await Run("loadout", "ini", "replace", "-l", id, "-f", "Skyrim.ini", "--text", "[General]\r\nsLanguage=r\u00f4le\r\n[Papyrus]\r\nbEnableLogging=1\r\n");

        exit.Should().Be(0, output);
        Plain(output).Should().Contain("Replaced the loadout tweak of Skyrim.ini (2 settings).").And.Contain(RestoreTail);
        var stored = (await UserTweakOf(loadout, "Skyrim.ini"))!.Value.Bytes;
        windows1252.GetString(stored).Should().Be("[General]\r\nsLanguage=r\u00f4le\r\n[Papyrus]\r\nbEnableLogging=1\r\n");
        stored.Should().Contain((byte)0xF4, "the character is one Windows-1252 byte, not UTF-8");
        LatestRevisionNumber(loadout.LoadoutId).Should().BeGreaterThan(before);

        var (clearExit, clear) = await Run("loadout", "ini", "replace", "-l", id, "-f", "Skyrim.ini", "--text", "");

        clearExit.Should().Be(0, clear);
        (await UserTweakOf(loadout, "Skyrim.ini"))!.Value.Bytes.Should().BeEmpty();
    }

    [Fact]
    public async Task ReplaceTakesTheTextFromAFileAndRefusesTwoSourcesOrNone()
    {
        var loadout = await CreateLoadout();
        await SeedBaseline(loadout, "Skyrim.ini", SkyrimText);
        var id = loadout.LoadoutId.ToString();
        var source = Path.Combine(Path.GetTempPath(), $"ini-replace-{Guid.NewGuid():N}.ini");
        await File.WriteAllTextAsync(source, "[Papyrus]\r\nbEnableTrace=1\r\n");
        try
        {
            var (exit, output) = await Run("loadout", "ini", "replace", "-l", id, "-f", "Skyrim.ini", "--from", source);
            var (bothExit, both) = await Run("loadout", "ini", "replace", "-l", id, "-f", "Skyrim.ini", "--from", source, "--text", "x");
            var (noneExit, none) = await Run("loadout", "ini", "replace", "-l", id, "-f", "Skyrim.ini");

            exit.Should().Be(0, output);
            (await UserTweakOf(loadout, "Skyrim.ini"))!.Value.Document.Get("Papyrus", "bEnableTrace").Should().Be("1");
            bothExit.Should().NotBe(0);
            both.Should().Contain("Give either --text or --from.");
            noneExit.Should().NotBe(0);
            none.Should().Contain("Give either --text or --from.");
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public async Task ReplaceWithTheSameTextWritesNoRestorePoint()
    {
        var loadout = await CreateLoadout();
        await SeedBaseline(loadout, "Skyrim.ini", SkyrimText);
        var id = loadout.LoadoutId.ToString();
        const string text = "[Papyrus]\r\nbEnableTrace=1\r\n";
        await Run("loadout", "ini", "replace", "-l", id, "-f", "Skyrim.ini", "--text", text);
        var revisions = LatestRevisionNumber(loadout.LoadoutId);

        var (exit, output) = await Run("loadout", "ini", "replace", "-l", id, "-f", "Skyrim.ini", "--text", text);

        exit.Should().Be(0, output);
        Plain(output).Should().Contain("The loadout tweak of Skyrim.ini already has that text.");
        LatestRevisionNumber(loadout.LoadoutId).Should().Be(revisions);
    }

    [Fact]
    public async Task ReplaceWarnsAboutAManagedKeyButStoresTheText()
    {
        var loadout = await CreateLoadout();
        await SeedBaseline(loadout, "Skyrim.ini", SkyrimText);

        var (exit, output) = await Run("loadout", "ini", "replace", "-l", loadout.LoadoutId.ToString(), "-f", "Skyrim.ini", "--text", "[Archive]\r\nbInvalidateOlderFiles=0\r\n");

        exit.Should().Be(0, output);
        Plain(output).Should().Contain("Note: The app sets [Archive] bInvalidateOlderFiles in Skyrim.ini itself");
        (await UserTweakOf(loadout, "Skyrim.ini"))!.Value.Document.Get("Archive", "bInvalidateOlderFiles").Should().Be("0");
    }

    /// <summary>
    /// The stand-ins in <see cref="McpTestHost"/> name their own options, so this sends each tool's argv through the
    /// real verbs and the real command line parser, in a server over a scratch sync file.
    /// </summary>
    [Fact]
    public async Task EveryIniToolsArgumentsParseAgainstTheRealVerbs()
    {
        var loadout = await CreateLoadout();
        await SeedBaseline(loadout, "Skyrim.ini", SkyrimText);
        var directory = Directory.CreateTempSubdirectory("mcp-ini-verbs").FullName;
        var syncFile = new SyncFile(NullLogger<SyncFile>.Instance, NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(directory, "test.sync")));
        using var server = new CliServer(NullLogger<CliServer>.Instance, ServiceProvider.GetRequiredService<CommandLineConfigurator>(), Substitute.For<ISettingsManager>(), syncFile, ServiceProvider);
        try
        {
            await server.StartCliServerAsync();
            var client = new CliClient(NullLogger<CliClient>.Instance, syncFile, ServiceProvider);
            var tools = McpToolTable.Create(new McpToolContext(new VerbRunner(client), McpLimits.Default, null, directory), readOnly: false);
            var id = loadout.LoadoutId.ToString();

            async Task<ToolResult> Call(string name, params (string Key, string Value)[] args)
            {
                var arguments = new JsonObject { ["loadout"] = id, ["file"] = "Skyrim.ini" };
                foreach (var (key, value) in args) arguments[key] = value;
                var result = await McpTestHost.CallAsync(tools, name, arguments);
                result.IsError.Should().BeFalse(name + ": " + result.Text);
                return result;
            }

            (await Call("loadout_ini_list")).Text.Should().Contain("Skyrim.ini");
            (await Call("loadout_ini_set", ("section", "Papyrus"), ("key", "bEnableTrace"), ("value", "1"))).Text.Should().Contain("bEnableTrace");
            (await Call("loadout_ini_show", ("part", "tweak"))).Text.Should().Contain("bEnableTrace=1");
            var layerId = (string)(await ListJson(loadout))["files"]![0]!["layers"]![0]!["fileId"]!;
            (await Call("loadout_ini_show", ("layer", layerId))).Text.Should().Contain("bEnableTrace=1");
            (await Call("loadout_ini_show")).Text.Should().Contain("sLanguage=ENGLISH");
            (await Call("loadout_ini_unset", ("section", "Papyrus"), ("key", "bEnableTrace"))).Text.Should().Contain("Removed");
            (await Call("loadout_ini_replace", ("text", "[Papyrus]\r\nbEnableLogging=1\r\n"))).Text.Should().Contain("Replaced");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            Directory.Delete(directory, recursive: true);
        }
    }
}
