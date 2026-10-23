using FluentAssertions;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Saves;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using NSubstitute;

namespace SilverModManager.Games.FromSoft.Tests.Saves;

public class SaveQuarantineTests : IDisposable
{
    private readonly SaveFixture _fixture = new();
    private readonly IProcessTracker _tracker = Substitute.For<IProcessTracker>();
    private readonly SaveBackups _backups;
    private readonly SaveQuarantine _quarantine;

    public SaveQuarantineTests()
    {
        _backups = new SaveBackups(FileSystem.Shared.FromUnsanitizedFullPath(_fixture.DataFolder), new SaveFolderScanner(EldenRingGame.Definition.SaveNames), new FakeTimeProvider(new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero)));
        _quarantine = new SaveQuarantine(_backups, _tracker);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void RefusesWhileSteamRunsBeforeChangingAnything()
    {
        var save = _fixture.Save("ER0000.sl2", "vanilla");
        _tracker.IsSteamRunning().Returns(true);
        var before = SaveFixture.Snapshot(_fixture.Root);

        var act = () => _quarantine.Quarantine(save, []);

        act.Should().Throw<SaveRefusedException>().WithMessage("*Close Steam*");
        SaveFixture.Snapshot(_fixture.Root).Should().BeEquivalentTo(before);
        Directory.Exists(_fixture.DataFolder).Should().BeFalse("not even the folder for the copies was created");
    }

    [Fact]
    public void BacksUpBeforeMovingTheSaveOutOfThePrefix()
    {
        var save = _fixture.Save("ER0000.sl2", "vanilla");
        var bytes = File.ReadAllBytes(save);

        var result = _quarantine.Quarantine(save, []);

        File.Exists(save).Should().BeFalse();
        var copies = Directory.GetFiles(Path.Combine(_fixture.DataFolder, "quarantine-backup"), "ER0000.sl2.*-orig");
        copies.Should().ContainSingle();
        File.ReadAllBytes(copies[0]).Should().Equal(bytes);
        result.MovedTo.Should().StartWith(Path.Combine(_fixture.DataFolder, "quarantine") + Path.DirectorySeparatorChar);
        File.ReadAllBytes(result.MovedTo!).Should().Equal(bytes);
    }

    [Fact]
    public void AFailedBackupLeavesTheSaveWhereItWas()
    {
        var save = _fixture.Save("ER0000.sl2", "vanilla");
        // A file where the folder for the copies has to be makes the backup fail.
        File.WriteAllText(Path.Combine(_fixture.Root, "data"), "in the way");

        var act = () => _quarantine.Quarantine(save, []);

        act.Should().Throw<IOException>();
        File.Exists(save).Should().BeTrue();
    }

    [Fact]
    public void QuarantinedSaveAndItsCopyCanBeListedForRestore()
    {
        var save = _fixture.Save("ER0000.sl2", "vanilla");

        _quarantine.Quarantine(save, []);

        _backups.List().Select(b => b.Location).Should().BeEquivalentTo([SaveBackupLocation.Quarantined, SaveBackupLocation.QuarantineBackup]);
    }

    [Fact]
    public void ReportsTheSteamCloudEntriesOfThisAccountAndFileOnly()
    {
        var save = _fixture.Save("ER0000.sl2", "vanilla");
        IReadOnlyList<CloudSave> cloud =
        [
            new("65369667", SaveFixture.Account, $"EldenRing/{SaveFixture.Account}/ER0000.sl2", 10, "Elden Ring"),
            new("65369667", SaveFixture.Account, $"EldenRing/{SaveFixture.Account}/ER0000.co2", 10, "Elden Ring"),
            new("11111111", "76561198000000099", "EldenRing/76561198000000099/ER0000.sl2", 10, "Elden Ring"),
        ];

        var result = _quarantine.Quarantine(save, cloud);

        var instructions = result.Notes.Where(n => n.Contains("Steam Cloud")).ToArray();
        instructions.Should().ContainSingle().Which.Should().Contain("account 65369667").And.Contain($"EldenRing/{SaveFixture.Account}/ER0000.sl2");
    }

    [Fact]
    public void ASaveThatIsAlreadyGoneStillReportsTheCloudEntries()
    {
        var path = Path.Combine(_fixture.SaveFolder, "ER0000.sl2");

        var result = _quarantine.Quarantine(path, [new("1", SaveFixture.Account, $"EldenRing/{SaveFixture.Account}/ER0000.sl2", 1, "Elden Ring")]);

        result.MovedTo.Should().BeNull();
        result.Notes.Should().HaveCount(2);
        Directory.Exists(_fixture.DataFolder).Should().BeFalse();
    }

    [Theory]
    [InlineData("ER0000.co2")]
    [InlineData("ER0000.sl2.bak")]
    [InlineData("ER0000_modded")]
    public void OnlyTheVanillaSaveCanBeQuarantined(string name)
    {
        var path = _fixture.Save(name, "x");
        var before = SaveFixture.Snapshot(_fixture.Root);

        var act = () => _quarantine.Quarantine(path, []);

        act.Should().Throw<SaveRefusedException>();
        SaveFixture.Snapshot(_fixture.Root).Should().BeEquivalentTo(before);
    }

    [Fact]
    public void FilesNextToTheSaveAreUntouched()
    {
        var unknown = _fixture.Save("ER0000_modded", "precious");
        var co2 = _fixture.Save("ER0000.co2", "coop");
        var save = _fixture.Save("ER0000.sl2", "vanilla");
        var before = (Unknown: File.ReadAllBytes(unknown), Coop: File.ReadAllBytes(co2));

        _quarantine.Quarantine(save, []);

        File.ReadAllBytes(unknown).Should().Equal(before.Unknown);
        File.ReadAllBytes(co2).Should().Equal(before.Coop);
    }
}
