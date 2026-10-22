using FluentAssertions;
using NexusMods.Games.CreationEngine.Tests.SortOrder;
using NexusMods.Sdk.Games;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.RoundTrip;

/// <summary>
/// The titles that write plugins.txt (active plugins) and loadorder.txt (every plugin in order) next to each
/// other: both reach the disk on one sync, list the same plugins, and keep a plugin that is off out of plugins.txt.
/// </summary>
public abstract class ATextfileRoundTripTest<TTest, TGame>(ITestOutputHelper helper) : ACreationEngineRoundTripTest<TTest, TGame>(helper)
    where TTest : class
    where TGame : class, NexusMods.Abstractions.Games.IGame
{
    protected override string Marker => "";
    protected override GamePath[] CompanionFiles => [LoadOrderFile];

    protected GamePath LoadOrderFile => PluginsFile.Parent / "loadorder.txt";

    /// <summary>Plugins that must lead both files, in order, whatever the mod order.</summary>
    protected virtual string[] LeadingPlugins => [];

    protected async Task<string[]> LoadOrderLines()
    {
        var file = OnDisk(LoadOrderFile);
        file.FileExists.Should().BeTrue();
        return (await file.ReadAllTextAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private async Task GameHas(GamePath path, string content)
    {
        var file = OnDisk(path);
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(content);
    }

    [Fact]
    public async Task BothFilesAreWrittenWithTheSamePluginsInOrder()
    {
        var loadout = await CreateLoadoutOn("default");
        foreach (var name in LeadingPlugins.Reverse()) await InstallPlugin(loadout, name);
        await InstallPlugin(loadout, "First.esp");
        await Apply(loadout);

        var order = await LoadOrderLines();
        var active = await PluginLines();

        order[0].Should().Be(Header);
        active[0].Should().Be(Header);
        string[] expected = [.. LeadingPlugins, "First.esp"];
        order.Skip(1).Should().Equal(expected);
        active.Skip(1).Should().Equal(expected);
    }

    [Fact]
    public async Task APluginTheLoadoutDoesNotHaveIsInNeitherFile()
    {
        var loadout = await CreateLoadoutOn("default");
        var first = await InstallPlugin(loadout, "First.esp");
        await InstallPlugin(loadout, "Second.esp");
        await Apply(loadout);

        await LoadoutManager.RemoveItems([first.LoadoutItemGroupId]);
        await Apply(loadout);

        (await LoadOrderLines()).Skip(1).Should().Equal("Second.esp");
        (await PluginLines()).Skip(1).Should().Equal("Second.esp");
    }

    [Fact]
    public async Task TheFirstApply_TakesTheOrderFromLoadOrderTxtAndTheSwitchesFromPluginsTxt()
    {
        var loadout = await CreateLoadoutOn("default");
        await InstallPlugin(loadout, "A.esp");
        await InstallPlugin(loadout, "B.esp");
        await InstallPlugin(loadout, "C.esp");
        var variety = Game.PluginVariety();
        await variety.Unseed(Refresh(loadout), Connection);
        // plugins.txt alone would give C, B: A's place only loadorder.txt knows.
        await GameHas(LoadOrderFile, "C.esp\nA.esp\nB.esp\n");
        await GameHas(PluginsFile, "C.esp\nB.esp\n");

        await Apply(loadout);

        (await LoadOrderLines()).Skip(1).Should().Equal("C.esp", "A.esp", "B.esp");
        (await PluginLines()).Skip(1).Should().Equal("C.esp", "B.esp");
    }

    [Fact]
    public async Task APluginSwitchedOffStaysInLoadOrderTxtAndLeavesPluginsTxt()
    {
        var loadout = await CreateLoadoutOn("default");
        await InstallPlugin(loadout, "A.esp");
        await InstallPlugin(loadout, "B.esp");
        await Apply(loadout);

        var variety = Game.PluginVariety();
        await variety.SetEnabled(variety.SortOrderOf(Refresh(loadout), Connection), PluginSortOrderTestHelpers.Key("A.esp"), false);
        await Apply(loadout);

        (await LoadOrderLines()).Skip(1).Should().Equal("A.esp", "B.esp");
        (await PluginLines()).Skip(1).Should().Equal("B.esp");
    }
}
