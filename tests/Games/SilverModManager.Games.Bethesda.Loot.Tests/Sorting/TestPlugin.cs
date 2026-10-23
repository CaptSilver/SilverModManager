using SilverModManager.Games.Bethesda.Loot.Metadata;
using SilverModManager.Games.Bethesda.Loot.Sorting;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Sorting;

// Ports the TestPlugin mock in libloot's sorting/mod.rs.
internal sealed class TestPlugin(string name) : ISortingPlugin
{
    private readonly List<string> _masters = [];
    private readonly List<string> _overlappingRecordPlugins = [];
    private readonly List<string> _overlappingAssetPlugins = [];

    public string Name { get; } = name;

    public bool IsMaster { get; set; }

    public bool IsBlueprintPlugin { get; set; }

    public int OverrideRecordCount { get; set; }

    public int AssetCount { get; set; }

    public IReadOnlyList<string> Masters => _masters;

    public void AddMaster(string pluginName) => _masters.Add(pluginName);

    public void AddOverlappingRecords(string pluginName) => _overlappingRecordPlugins.Add(pluginName);

    public void AddOverlappingAssets(string pluginName) => _overlappingAssetPlugins.Add(pluginName);

    public bool DoRecordsOverlap(ISortingPlugin other) => _overlappingRecordPlugins.Contains(other.Name);

    public bool DoAssetsOverlap(ISortingPlugin other) => _overlappingAssetPlugins.Contains(other.Name);
}

// Ports the Fixture in libloot's sorting/plugins.rs tests.
internal sealed class SortingFixture
{
    public const string PluginA = "A.esp";
    public const string PluginB = "B.esp";
    public const string PluginC = "C.esp";
    public const string PluginD = "D.esp";
    public const string PluginE = "E.esp";

    private readonly Dictionary<string, (TestPlugin Plugin, int Index)> _plugins;

    private SortingFixture(GroupsGraph groupsGraph, Dictionary<string, (TestPlugin, int)> plugins)
    {
        GroupsGraph = groupsGraph;
        _plugins = plugins;
    }

    public GroupsGraph GroupsGraph { get; }

    public static SortingFixture WithPlugins(params string[] pluginNames)
    {
        Group[] masterlist =
        [
            new("A"),
            new("B") { AfterGroups = ["A"] },
            new("C"),
            new("default") { AfterGroups = ["C"] },
            new("E") { AfterGroups = ["default"] },
            new("F") { AfterGroups = ["E"] },
        ];
        Group[] userlist = [new("C") { AfterGroups = ["B"] }];

        return new SortingFixture(
            GroupsGraph.Build(masterlist, userlist),
            pluginNames.Select((n, i) => (Name: n, Value: (new TestPlugin(n), i))).ToDictionary(e => e.Name, e => e.Value));
    }

    public TestPlugin Plugin(string name) => _plugins[name].Plugin;

    public PluginSortingData<TestPlugin> SortingData(string name) =>
        new(_plugins[name].Plugin, null, null, _plugins[name].Index);

    public PluginSortingData<TestPlugin> GroupSortingData(string name, string groupName) =>
        new(_plugins[name].Plugin, new PluginMetadata(name) { Group = groupName }, null, _plugins[name].Index);

    public PluginSortingData<TestPlugin> UserGroupSortingData(string name, string groupName) =>
        new(_plugins[name].Plugin, null, new PluginMetadata(name) { Group = groupName }, _plugins[name].Index);
}
