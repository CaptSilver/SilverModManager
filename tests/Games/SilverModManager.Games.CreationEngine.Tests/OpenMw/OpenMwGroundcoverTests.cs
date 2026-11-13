using FluentAssertions;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.CreationEngine.SortOrder;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Games.CreationEngine.Tests.SortOrder;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.OpenMw;

public class OpenMwGroundcoverTests(ITestOutputHelper helper) : AOpenMwGameTest<OpenMwGroundcoverTests>(helper)
{
    private const string Source = "SilverModManager.Games.CreationEngine";

    private IGroundcoverSortOrderVariety Variety => Game.SortOrderManager.GetSortOrderVarieties().OfType<IGroundcoverSortOrderVariety>().Single();

    private async Task SetGroundcover(Loadout.ReadOnly loadout, string plugin, bool on) =>
        await Variety.SetGroundcover(((IPluginSortOrderVariety)Variety).SortOrderOf(Refresh(loadout), Connection), PluginSortOrderTestHelpers.Key(plugin), on);

    private async Task<string[]> LinesOf(string key) =>
        (await Config.ReadAllTextAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith(key + "=")).ToArray();

    [Fact]
    public async Task SettingGroundcoverOnARowWritesAGroundcoverLine()
    {
        var loadout = await CreateLoadout();
        await InstallPlugin(loadout, "Grass.esp");
        await InstallPlugin(loadout, "Mod.esp");
        await Apply(loadout);
        (await ContentLines()).Should().Contain("content=Grass.esp");

        await SetGroundcover(loadout, "Grass.esp", true);
        await Apply(loadout);

        (await LinesOf("groundcover")).Should().Equal("groundcover=Grass.esp");
        (await ContentLines()).Should().Contain("content=Mod.esp").And.NotContain("content=Grass.esp");

        await SetGroundcover(loadout, "Grass.esp", false);
        await Apply(loadout);

        (await LinesOf("groundcover")).Should().BeEmpty();
        (await ContentLines()).Should().Contain("content=Grass.esp");
    }

    [Fact]
    public async Task GroundcoverRowsWarnWhileSettingsCfgHasItOff()
    {
        var loadout = await CreateLoadout();
        await InstallPlugin(loadout, "Grass.esp");
        await Apply(loadout);
        await SetGroundcover(loadout, "Grass.esp", true);

        var warning = (await Game.Diagnose(Refresh(loadout))).Should().ContainSingle(d => d.Id.Source == Source && d.Id.Number == 15).Subject;

        warning.Severity.Should().Be(DiagnosticSeverity.Warning);
        warning.FormatSummary(new PlainDiagnosticWriter()).Should().Contain("Groundcover is off in OpenMW's settings.cfg");
    }

    [Fact]
    public async Task NoWarningWhenSettingsCfgEnablesIt()
    {
        File.WriteAllText((Roots.NativeConfig / "settings.cfg").GetFullPath(), "[Camera]\nenabled = false\n\n[Groundcover]\n\n# enable separate groundcover handling\nenabled = true\n");
        var loadout = await CreateLoadout();
        await InstallPlugin(loadout, "Grass.esp");
        await Apply(loadout);
        await SetGroundcover(loadout, "Grass.esp", true);

        (await Game.Diagnose(Refresh(loadout))).Should().NotContain(d => d.Id.Source == Source && d.Id.Number == 15);
    }

    [Fact]
    public async Task NoWarningWhenNoRowIsGroundcover()
    {
        var loadout = await CreateLoadout();
        await InstallPlugin(loadout, "Grass.esp");

        (await Game.Diagnose(Refresh(loadout))).Should().NotContain(d => d.Id.Source == Source && d.Id.Number == 15);
    }
}

/// <summary>
/// openmw.cfg already loads a plugin from the app's data folder (data-local) as groundcover before the app manages
/// the game: the seed keeps it groundcover, so a later Apply does not turn the line into a content= line.
/// </summary>
public class OpenMwGroundcoverSeedTests(ITestOutputHelper helper) : AOpenMwGameTest<OpenMwGroundcoverSeedTests>(helper)
{
    protected override string[] PluginsInDataLocal => ["Grass.esp", "Mod.esp"];

    protected override string ConfigText => Install.Config("content=Mod.esp\ngroundcover=Grass.esp\n");

    [Fact]
    public async Task ApplyingANewModKeepsAGroundcoverLine()
    {
        var loadout = await CreateLoadout();
        await Apply(loadout);

        // A new plugin makes the app rewrite openmw.cfg.
        await InstallPlugin(loadout, "New.esp");
        await Apply(loadout);

        var text = await Config.ReadAllTextAsync();
        var lines = text.Split('\n');
        lines.Should().Contain("content=New.esp", text);
        lines.Should().Contain("groundcover=Grass.esp", text).And.NotContain("content=Grass.esp", text);
        lines.Should().Contain("content=Mod.esp", text);
    }
}

public class OpenMwSettingsTests
{
    [Theory]
    [InlineData("[Groundcover]\nenabled = true\n", true)]
    [InlineData("[Groundcover]\r\n\r\n# enable separate groundcover handling\r\nenabled=TRUE\r\n", true)]
    [InlineData("[Groundcover]\nenabled = false\n", false)]
    [InlineData("[Post Processing]\nenabled = true\n", false)]
    [InlineData("[Groundcover]\nenabled = true\n[Post Processing]\nenabled = false\n", true)]
    [InlineData("[Groundcover]\nenabled = true\nenabled = false\n", false)]
    [InlineData("[Groundcover]\nenabled = 1\n", false)]
    [InlineData("", false)]
    public void OnlyTheEnabledKeyOfTheGroundcoverSectionCounts(string text, bool expected) =>
        CreationEngine.OpenMw.OpenMwSettings.GroundcoverEnabled(text).Should().Be(expected);
}
