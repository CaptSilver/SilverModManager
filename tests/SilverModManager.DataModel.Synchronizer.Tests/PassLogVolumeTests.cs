using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A pass runs every time the status is looked at, so what it logs is multiplied by the session length. The pass
/// says what it found in one line; the line per file is on its own category.
/// </summary>
public class PassLogVolumeTests(ITestOutputHelper helper) : AStubbedGameTest<PassLogVolumeTests>(helper)
{
    private const int FileCount = 30;

    private readonly CapturedLogs _logs = new();

    protected override IServiceCollection AddServices(IServiceCollection services) => _logs.AddTo(base.AddServices(services));

    private async Task<Loadout.ReadOnly> LoadoutWithPendingMod()
    {
        var loadout = await CreateLoadout();
        var files = Enumerable.Range(0, FileCount).ToDictionary(i => (RelativePath)$"Data/file{i}.txt", _ => "content"u8.ToArray());
        await using var archiveFile = await CreateTestArchive(files);
        var archive = await RegisterLocalArchive(archiveFile.Path);
        await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout, installer: new StubbedGameInstaller(ServiceProvider));
        return Refresh(loadout);
    }

    private async Task Evaluate(Loadout.ReadOnly loadout)
    {
        var metadata = GameRegistry.ForceGetMetadata(GameInstallation);
        await Synchronizer.EvaluateSynchronize(Refresh(loadout), Synchronizer.GetLastScannedDiskState(metadata), Synchronizer.GetDiskStateForGame(metadata));
    }

    private IEnumerable<CapturedLine> PendingLines() => _logs.Lines.Where(l => l.Message.StartsWith("Pending in loadout"));

    [Fact]
    public async Task APassOverManyPendingFilesWritesOneSummaryLine()
    {
        var loadout = await LoadoutWithPendingMod();

        await Evaluate(loadout);

        var summary = PendingLines().Where(l => l.Category != SynchronizerLogging.PerFileCategory).ToArray();
        summary.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Trace);
        summary[0].Message.Should().Contain($"{FileCount} file");
    }

    [Fact]
    public async Task ThePerFileLinesAreOnTheirOwnCategory()
    {
        var loadout = await LoadoutWithPendingMod();

        await Evaluate(loadout);

        var perFile = PendingLines().Where(l => l.Category == SynchronizerLogging.PerFileCategory).ToArray();
        perFile.Should().HaveCount(FileCount);
        perFile.Should().OnlyContain(l => l.Level == LogLevel.Trace);
        perFile.Should().Contain(l => l.Message.Contains("file7.txt"));
    }

    [Fact]
    public async Task APassThatFindsNothingPendingLogsNothing()
    {
        var loadout = await LoadoutWithPendingMod();
        await Synchronizer.Synchronize(loadout);
        var before = _logs.Lines.Count;

        await Evaluate(loadout);

        _logs.Lines.Skip(before).Where(l => l.Message.StartsWith("Pending in loadout")).Should().BeEmpty();
    }
}
