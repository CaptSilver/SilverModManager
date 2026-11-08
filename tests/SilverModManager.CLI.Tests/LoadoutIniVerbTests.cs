using System.Text.Json.Nodes;
using System.Text;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;
using Microsoft.Extensions.DependencyInjection;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>loadout ini list</c> and <c>loadout ini show</c> read a loadout's copies of the game's INI files, the tweak
/// layers laid over them and the bytes Apply would write.
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
}
