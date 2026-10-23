using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.Larian.BaldursGate3.Models;
using NexusMods.Games.Larian.Tests.BaldursGate3.Modsettings;
using NexusMods.MnemonicDB.Abstractions;

namespace NexusMods.Games.Larian.Tests.BaldursGate3.GameInstalled;

/// <summary>
/// A game-installed pak keeps the <c>modsettings.lsx</c> entry the game gave it, and the writer brings that exact
/// entry back when the group is switched on again.
/// </summary>
public class Bg3IdentityTests(ITestOutputHelper helper) : ModsettingsFixture(helper)
{
    [Fact]
    public async Task SwitchingTheGroupOffRemovesTheEntryAndSwitchingItOnWritesTheStoredOne()
    {
        await WriteGameInstalledPak();
        var loadout = await CreateLoadout();
        await Apply(loadout);
        var item = GameInstalledGroupOf(loadout).AsLoadoutItemGroup().Children.OfTypeLoadoutItemGroup().OfTypeGameInstalledItem()
            .Single(i => i.SourceId == GameModPak);
        item.TryGetAsBg3ModuleEntry(out var stored).Should().BeTrue();

        await DisableItem(GameInstalledGroupOf(loadout).Id);
        await Apply(loadout);
        Disk().Entries.Should().NotContain(e => e.Uuid == GameModUuid);

        await EnableItem(GameInstalledGroupOf(loadout).Id);
        await Apply(loadout);

        var written = Disk().Entries.Single(e => e.Uuid == GameModUuid);
        written.Md5.Should().Be(GameModMd5).And.Be(stored.Md5.Value);
        written.Folder.Should().Be(stored.Folder.Value);
        written.Name.Should().Be(stored.ModuleName.Value);
        written.PublishHandle.Should().Be(stored.PublishHandle.Value);
        written.Version64.Should().Be(stored.Version64.Value);
    }
}
