using FluentAssertions;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Saves;
using NexusMods.Paths;

namespace SilverModManager.Games.FromSoft.Tests.Saves;

public class SaveBackupsTests : IDisposable
{
    private readonly SaveFixture _fixture = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _fixture.Dispose();

    private SaveBackups Backups => new(FileSystem.Shared.FromUnsanitizedFullPath(_fixture.DataFolder), new SaveFolderScanner(EldenRingGame.Definition.SaveNames), _clock);

    private string Dir(string name) => Path.Combine(_fixture.DataFolder, name);

    [Fact]
    public void BackupCopiesTheSaveUnderAStampedNameWithTheLabel()
    {
        var save = _fixture.Save("ER0000.co2", "before the boss");

        var backup = Backups.Backup(save, "preboss");

        Path.GetFileName(backup.Path).Should().MatchRegex(@"^ER0000\.co2\.\d{8}-\d{6}-preboss$");
        Path.GetDirectoryName(backup.Path).Should().Be(Dir("backups"));
        File.ReadAllBytes(backup.Path).Should().Equal(File.ReadAllBytes(save));
        backup.Should().Match<SaveBackup>(b => b.SaveName == "ER0000.co2" && b.Label == "preboss");
    }

    [Fact]
    public void ListParsesTheNamesBackAndNamesNewestFirst()
    {
        var save = _fixture.Save("ER0000.sl2", "x");
        var first = Backups.Backup(save, "one");
        _clock.Advance(TimeSpan.FromMinutes(5));
        var second = Backups.Backup(save);

        var listed = Backups.List();

        listed.Select(b => b.Path).Should().Equal(second.Path, first.Path);
        listed[0].Should().Match<SaveBackup>(b => b.SaveName == "ER0000.sl2" && b.Label == "" && b.Stamp == second.Stamp);
        listed[1].Label.Should().Be("one");
    }

    [Fact]
    public void ListIgnoresFilesThatAreNotCopiesOfAKnownSave()
    {
        var save = _fixture.Save("ER0000.sl2", "x");
        Backups.Backup(save);
        File.WriteAllText(Path.Combine(Dir("backups"), "notes.txt"), "mine");
        File.WriteAllText(Path.Combine(Dir("backups"), "ER0000_modded.20260714-120000"), "unknown save");

        Backups.List().Should().ContainSingle();
    }

    [Fact]
    public void TwoCopiesInTheSameSecondDoNotReplaceEachOther()
    {
        var save = _fixture.Save("ER0000.sl2", "x");

        var first = Backups.Backup(save, "same");
        var second = Backups.Backup(save, "same");

        second.Path.Should().NotBe(first.Path);
        Directory.GetFiles(Dir("backups")).Should().HaveCount(2);
    }

    [Fact]
    public void ListIsEmptyBeforeAnythingWasBackedUp() => Backups.List().Should().BeEmpty();

    [Fact]
    public void BackupRefusesAFileItDoesNotKnow()
    {
        var unknown = _fixture.Save("ER0000_modded", "x");

        var act = () => Backups.Backup(unknown);

        act.Should().Throw<SaveRefusedException>();
        Directory.Exists(Dir("backups")).Should().BeFalse();
    }

    [Fact]
    public void RestoringACo2BackupLandsOnTheCo2AndKeepsWhatWasThere()
    {
        var co2 = _fixture.Save("ER0000.co2", "old");
        var backup = Backups.Backup(co2, "good");
        File.WriteAllBytes(co2, SaveFixture.BytesOf("newer"));
        var sl2 = _fixture.Save("ER0000.sl2", "vanilla");
        var vanilla = File.ReadAllBytes(sl2);

        var result = Backups.Restore(backup, _fixture.SaveFolder);

        result.Destination.Should().Be(co2);
        File.ReadAllBytes(co2).Should().Equal(SaveFixture.BytesOf("old"));
        File.ReadAllBytes(sl2).Should().Equal(vanilla);
        result.PreRestoreBackup.Should().NotBeNull();
        result.PreRestoreBackup!.Label.Should().Be("pre-restore");
        File.ReadAllBytes(result.PreRestoreBackup.Path).Should().Equal(SaveFixture.BytesOf("newer"));
    }

    [Fact]
    public void RestoringAnSl2BackupLandsOnTheSl2AndKeepsWhatWasThere()
    {
        var sl2 = _fixture.Save("ER0000.sl2", "old");
        var backup = Backups.Backup(sl2);
        File.WriteAllBytes(sl2, SaveFixture.BytesOf("newer"));

        var result = Backups.Restore(backup, _fixture.SaveFolder);

        File.ReadAllBytes(sl2).Should().Equal(SaveFixture.BytesOf("old"));
        File.ReadAllBytes(result.PreRestoreBackup!.Path).Should().Equal(SaveFixture.BytesOf("newer"));
        Directory.GetFiles(_fixture.SaveFolder).Should().ContainSingle("no temporary file is left behind");
    }

    [Fact]
    public void RestoringIntoAFolderWithoutTheSaveNeedsNoPreRestoreBackup()
    {
        var backup = Backups.Backup(_fixture.Save("ER0000.sl2", "old"));
        File.Delete(Path.Combine(_fixture.SaveFolder, "ER0000.sl2"));

        var result = Backups.Restore(backup, _fixture.SaveFolder);

        result.PreRestoreBackup.Should().BeNull();
        File.Exists(Path.Combine(_fixture.SaveFolder, "ER0000.sl2")).Should().BeTrue();
    }

    [Fact]
    public void ACopyOfTheModdedSaveNeverLandsOnTheVanillaSave()
    {
        var modded = _fixture.Save(EldenRingPaths.ModdedSave, "modded");
        var vanilla = _fixture.Save("ER0000.sl2", "vanilla");
        var before = File.ReadAllBytes(vanilla);

        var result = Backups.Restore(Backups.Backup(modded), _fixture.SaveFolder);

        result.Destination.Should().Be(modded);
        File.ReadAllBytes(vanilla).Should().Equal(before);
    }

    [Fact]
    public void ASaveNamedForALoadoutIsBackedUpAndRestoredToItsOwnName()
    {
        const string name = "ER0000_00000000000abc.sl2";
        var save = _fixture.Save(name, "loadout save");
        var vanilla = _fixture.Save("ER0000.sl2", "vanilla");
        var before = File.ReadAllBytes(vanilla);
        var backup = Backups.Backup(save);
        File.Delete(save);

        var result = Backups.Restore(backup, _fixture.SaveFolder);

        backup.SaveName.Should().Be(name);
        Backups.List().Should().ContainSingle().Which.SaveName.Should().Be(name);
        result.Destination.Should().Be(save);
        File.ReadAllBytes(save).Should().Equal(SaveFixture.BytesOf("loadout save"));
        File.ReadAllBytes(vanilla).Should().Equal(before);
    }

    [Fact]
    public void RestoreOfACopyNamedForAnUnknownFileIsRefusedAndTouchesNothing()
    {
        _fixture.Save("ER0000.sl2", "vanilla");
        Directory.CreateDirectory(Dir("backups"));
        var stray = Path.Combine(Dir("backups"), "ER0000_modded.20260714-120000");
        File.WriteAllText(stray, "x");
        var before = SaveFixture.Snapshot(_fixture.Root);

        var act = () => Backups.Restore(new SaveBackup(stray, "ER0000_modded", "20260714-120000", "", SaveBackupLocation.Backups), _fixture.SaveFolder);

        act.Should().Throw<SaveRefusedException>();
        SaveFixture.Snapshot(_fixture.Root).Should().BeEquivalentTo(before);
    }

    [Fact]
    public void RestoreOfACopyThatIsGoneIsRefusedBeforeAnyPreRestoreBackupIsTaken()
    {
        _fixture.Save("ER0000.sl2", "vanilla");
        var missing = new SaveBackup(Path.Combine(Dir("backups"), "ER0000.sl2.20260714-120000"), "ER0000.sl2", "20260714-120000", "", SaveBackupLocation.Backups);

        var act = () => Backups.Restore(missing, _fixture.SaveFolder);

        act.Should().Throw<SaveRefusedException>();
        Directory.Exists(Dir("backups")).Should().BeFalse();
    }

    [Fact]
    public void AnUnknownFileNamedLikeTheRestoreStagingFileIsUntouched()
    {
        var sl2 = _fixture.Save("ER0000.sl2", "old");
        var backup = Backups.Backup(sl2);
        var stray = _fixture.Save("ER0000.sl2.restore", "someone else's");
        var before = File.ReadAllBytes(stray);

        Backups.Restore(backup, _fixture.SaveFolder);

        File.ReadAllBytes(stray).Should().Equal(before);
        File.ReadAllBytes(sl2).Should().Equal(SaveFixture.BytesOf("old"));
        Directory.GetFiles(_fixture.SaveFolder).Should().HaveCount(2, "the staging file is gone and the stray one stays");
    }

    [Fact]
    public void AnUnknownFileInTheSaveFolderIsUntouchedByBackupAndRestore()
    {
        var unknown = _fixture.Save("ER0000_modded", "precious");
        var sl2 = _fixture.Save("ER0000.sl2", "vanilla");
        var before = File.ReadAllBytes(unknown);
        var stamp = File.GetLastWriteTimeUtc(unknown);

        Backups.Restore(Backups.Backup(sl2), _fixture.SaveFolder);

        File.ReadAllBytes(unknown).Should().Equal(before);
        File.GetLastWriteTimeUtc(unknown).Should().Be(stamp);
    }
}
