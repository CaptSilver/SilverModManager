using NexusMods.Games.Bethesda.Loot.Masterlists;

namespace NexusMods.Games.Bethesda.Loot.Tests.Masterlists;

public class LootMasterlistSettingsTests
{
    [Fact]
    public void RefreshIsOnByDefault()
    {
        new LootMasterlistSettings().LootMasterlistRefresh.Should().BeTrue();
    }

    [Fact]
    public void SettingsIdUsesTheProductNamespace()
    {
        LootMasterlistSettings.SettingsId.Should().StartWith("SilverModManager.");
    }
}
