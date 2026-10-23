using SilverModManager.Games.Bethesda.Loot.Metadata;
using SilverModManager.Games.Bethesda.Loot.Sorting;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Sorting;

// Ports the plugin_sorting_data tests in libloot's sorting/plugins.rs.
public class PluginSortingDataTests
{
    [Fact]
    public void IsBlueprintMasterShouldBeTrueIfAPluginIsAMasterAndABlueprintPlugin()
    {
        var master = new TestPlugin("Blank.esm") { IsMaster = true };
        var blueprintPlugin = new TestPlugin("Blank.esm") { IsBlueprintPlugin = true };
        var blueprintMaster = new TestPlugin("Blank.esm") { IsMaster = true, IsBlueprintPlugin = true };

        new PluginSortingData<TestPlugin>(master, null, null, 0).IsBlueprintMaster.Should().BeFalse();
        new PluginSortingData<TestPlugin>(blueprintPlugin, null, null, 0).IsBlueprintMaster.Should().BeFalse();
        new PluginSortingData<TestPlugin>(blueprintMaster, null, null, 0).IsBlueprintMaster.Should().BeTrue();
    }

    [Fact]
    public void GroupShouldPreferUserMetadataThenMasterlistThenDefault()
    {
        var plugin = new TestPlugin("A.esp");
        var masterlist = new PluginMetadata("A.esp") { Group = "masterlist group" };
        var user = new PluginMetadata("A.esp") { Group = "user group" };

        var both = new PluginSortingData<TestPlugin>(plugin, masterlist, user, 0);
        both.Group.Should().Be("user group");
        both.GroupIsUserMetadata.Should().BeTrue();

        var masterlistOnly = new PluginSortingData<TestPlugin>(plugin, masterlist, new PluginMetadata("A.esp"), 0);
        masterlistOnly.Group.Should().Be("masterlist group");
        masterlistOnly.GroupIsUserMetadata.Should().BeFalse();

        var neither = new PluginSortingData<TestPlugin>(plugin, null, null, 0);
        neither.Group.Should().Be(Group.DefaultName);
        neither.GroupIsUserMetadata.Should().BeFalse();
    }

    [Fact]
    public void FilenamesShouldComeFromLoadAfterAndRequirementMetadata()
    {
        var plugin = new TestPlugin("A.esp");
        var masterlist = new PluginMetadata("A.esp")
        {
            LoadAfter = [new File("la1.esp")],
            Requirements = [new File("req1.esp")],
        };
        var user = new PluginMetadata("A.esp")
        {
            LoadAfter = [new File("la2.esp")],
            Requirements = [new File("req2.esp")],
        };

        var data = new PluginSortingData<TestPlugin>(plugin, masterlist, user, 3);

        data.MasterlistLoadAfter.Should().Equal("la1.esp");
        data.UserLoadAfter.Should().Equal("la2.esp");
        data.MasterlistRequirements.Should().Equal("req1.esp");
        data.UserRequirements.Should().Equal("req2.esp");
        data.LoadOrderIndex.Should().Be(3);
    }
}
