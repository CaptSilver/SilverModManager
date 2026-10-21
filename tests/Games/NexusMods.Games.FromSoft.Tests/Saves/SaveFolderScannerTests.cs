using FluentAssertions;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Saves;

namespace NexusMods.Games.FromSoft.Tests.Saves;

public class SaveFolderScannerTests : IDisposable
{
    private readonly SaveFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void KnownSavesAndTheirBackupsAreClassifiedAndEverythingElseIsUnknown()
    {
        foreach (var name in new[] { "ER0000.sl2", "ER0000.sl2.bak", "ER0000.co2", "ER0000.co2.bak", EldenRingPaths.ModdedSave, EldenRingPaths.ModdedSave + ".bak", "ER0000_modded", "notes.txt" })
            _fixture.Save(name, name);

        var files = SaveFolderScanner.Scan(_fixture.SaveFolder).ToDictionary(f => f.Name);

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

        SaveFolderScanner.Scan(_fixture.SaveFolder).Select(f => f.Name).Should().Equal("ER0000.sl2", "ER0000_modded");
    }

    [Fact]
    public void ANameThatOnlyLooksLikeASaveIsUnknown()
    {
        foreach (var name in new[] { "er0000.sl2", "ER0000.sl2.bak2", "ER0000.sl2.1", "xER0000.co2" })
            SaveFolderScanner.KindOf(name, out _).Should().Be(SaveKind.Unknown, name);
    }

    [Fact]
    public void AFolderThatDoesNotExistHasNoFiles()
        => SaveFolderScanner.Scan(Path.Combine(_fixture.Root, "missing")).Should().BeEmpty();
}
