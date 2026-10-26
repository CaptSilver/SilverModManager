using FluentAssertions;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Saves;

namespace SilverModManager.Games.FromSoft.Tests.Saves;

public class SaveFolderScannerLoadoutSlotTests : IDisposable
{
    private const string Slot = "0a1b2c3d4e5f60";
    private readonly SaveFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void ASaveNamedForALoadoutCarriesTheLoadoutsSlotAndItsBackupDoesToo()
    {
        _fixture.Save($"ER0000_{Slot}.sl2", "mine");
        _fixture.Save($"ER0000_{Slot}.sl2.bak", "mine");

        var files = new SaveFolderScanner(EldenRingGame.Definition.SaveNames).Scan(_fixture.SaveFolder);

        files.Should().HaveCount(2).And.AllSatisfy(f => f.LoadoutSlot.Should().Be(Slot));
    }

    [Fact]
    public void SavesThatBelongToNoLoadoutCarryNoSlot()
    {
        foreach (var name in new[] { "ER0000.sl2", "ER0000.co2", EldenRingPaths.ModdedSave, "ER0000_modded", "ER0000_NOTHEX.sl2" })
            _fixture.Save(name, name);

        var files = new SaveFolderScanner(EldenRingGame.Definition.SaveNames).Scan(_fixture.SaveFolder);

        files.Should().OnlyContain(f => f.LoadoutSlot == null);
    }
}
