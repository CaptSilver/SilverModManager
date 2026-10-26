using FluentAssertions;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Saves;

namespace SilverModManager.Games.FromSoft.Tests.Saves;

public class SaveFolderScannerTests : IDisposable
{
    private readonly SaveFixture _fixture = new();
    private static readonly SaveFolderScanner Scanner = new(EldenRingGame.Definition.SaveNames);

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void KnownSavesAndTheirBackupsAreClassifiedAndEverythingElseIsUnknown()
    {
        foreach (var name in new[] { "ER0000.sl2", "ER0000.sl2.bak", "ER0000.co2", "ER0000.co2.bak", EldenRingPaths.ModdedSave, EldenRingPaths.ModdedSave + ".bak", "ER0000_modded", "notes.txt" })
            _fixture.Save(name, name);

        var files = Scanner.Scan(_fixture.SaveFolder).ToDictionary(f => f.Name);

        files["ER0000.sl2"].Should().Match<SaveFileEntry>(f => f.Kind == SaveKind.Vanilla && !f.IsBackup);
        files["ER0000.sl2.bak"].Should().Match<SaveFileEntry>(f => f.Kind == SaveKind.Vanilla && f.IsBackup);
        files["ER0000.co2"].Kind.Should().Be(SaveKind.SeamlessCoop);
        files["ER0000.co2.bak"].Should().Match<SaveFileEntry>(f => f.Kind == SaveKind.SeamlessCoop && f.IsBackup);
        files[EldenRingPaths.ModdedSave].Kind.Should().Be(SaveKind.Modded);
        files[EldenRingPaths.ModdedSave + ".bak"].Should().Match<SaveFileEntry>(f => f.Kind == SaveKind.Modded && f.IsBackup);
        files["ER0000_modded"].Should().Match<SaveFileEntry>(f => f.Kind == SaveKind.Unknown && !f.IsKnown);
        files["notes.txt"].Kind.Should().Be(SaveKind.Unknown);
    }

    [Fact]
    public void UnknownFilesAreListedAfterTheKnownOnes()
    {
        _fixture.Save("ER0000_modded", "x");
        _fixture.Save("ER0000.sl2", "x");

        Scanner.Scan(_fixture.SaveFolder).Select(f => f.Name).Should().Equal("ER0000.sl2", "ER0000_modded");
    }

    [Fact]
    public void ANameThatOnlyLooksLikeASaveIsUnknown()
    {
        foreach (var name in new[] { "er0000.sl2", "ER0000.sl2.bak2", "ER0000.sl2.1", "xER0000.co2" })
            Scanner.KindOf(name, out _).Should().Be(SaveKind.Unknown, name);
    }

    [Fact]
    public void ASaveNamedForALoadoutSlotAndItsBackupAreModded()
    {
        Scanner.KindOf("ER0000_00000000000abc.sl2", out var isBackup).Should().Be(SaveKind.Modded);
        isBackup.Should().BeFalse();
        Scanner.KindOf("ER0000_00000000000abc.sl2.bak", out var backup).Should().Be(SaveKind.Modded);
        backup.Should().BeTrue();
    }

    [Fact]
    public void ANameThatOnlyResemblesALoadoutSlotStaysUnknown()
    {
        // One character too many or too few, upper case, a non-hex digit, another extension, and the names other tools wrote.
        foreach (var name in new[]
                 {
                     "ER0000_00000000000abcd.sl2", "ER0000_0000000000abc.sl2", "ER0000_00000000000ABC.sl2", "ER0000_0000000000000g.sl2",
                     "ER0000_00000000000abc.co2", "ER0000_00000000000abc.sl2.bak2", "ER0000_backup.sl2", "ER0000_modded.sl2", "XR0000_00000000000abc.sl2",
                 })
            Scanner.KindOf(name, out _).Should().Be(SaveKind.Unknown, name);
    }

    [Fact]
    public void ASlotNameIsOnlyModdedForAGameWithAModdedSave()
    {
        var scanner = new SaveFolderScanner(new FromSoftSaveNames("TS0000.sl2", null, null));

        scanner.KindOf("TS0000_00000000000abc.sl2", out _).Should().Be(SaveKind.Unknown);
    }

    [Fact]
    public void AFolderThatDoesNotExistHasNoFiles()
        => Scanner.Scan(Path.Combine(_fixture.Root, "missing")).Should().BeEmpty();

    [Fact]
    public void AScannerClassifiesTheNamesOfItsOwnGame()
    {
        var scanner = new SaveFolderScanner(new FromSoftSaveNames("TS0000.sl2", "TS0000_silver.sl2", null));

        scanner.KindOf("TS0000.sl2", out var vanillaBackup).Should().Be(SaveKind.Vanilla);
        vanillaBackup.Should().BeFalse();
        scanner.KindOf("TS0000.sl2.bak", out var backup).Should().Be(SaveKind.Vanilla);
        backup.Should().BeTrue();
        scanner.KindOf("TS0000_silver.sl2", out _).Should().Be(SaveKind.Modded);
        scanner.KindOf("ER0000.sl2", out _).Should().Be(SaveKind.Unknown);
        scanner.KindOf("ER0000.co2", out _).Should().Be(SaveKind.Unknown);
    }

    [Fact]
    public void AGameWithoutAModdedSaveNeverYieldsTheModdedKind()
    {
        var scanner = new SaveFolderScanner(new FromSoftSaveNames("TS0000.sl2", null, null));

        scanner.KindOf("TS0000_silver.sl2", out _).Should().Be(SaveKind.Unknown);
    }
}
