using FluentAssertions;
using Microsoft.Extensions.Logging;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Library;

namespace SilverModManager.Wabbajack.Tests.Acquire;

public class ModlistDownloadsTests
{
    private static readonly ModlistInfo Skyrim = FakeCatalog.Info(0xA0A, "Skyrim list");
    private static readonly ModlistInfo Fallout = FakeCatalog.Info(0xB0B, "Fallout list");

    private readonly SilverModManager.Sdk.Settings.ISettingsManager _settings = MemorySettings.Create();
    private readonly FakePlanner _planner = new();
    private readonly RecordingAcquirer _acquirer = new();
    private readonly ListLogger<ModlistDownloads> _log = new();
    private readonly ModlistDownloads _downloads;

    public ModlistDownloadsTests()
    {
        _downloads = new ModlistDownloads(_settings, _planner, _acquirer, new FakeCatalog(Skyrim, Fallout), _log);
    }

    private static ArchivePlanRow Row(ArchiveState state, string name)
        => new(new ArchiveEntry(FileIdentity.XxHash64(NexusMods.Hashing.xxHash3.Hash.From((ulong)name.GetHashCode())), 100, name, "", new HttpState(new Uri("https://example.test/" + name), [])), state, null, null, null);

    private static ArchivePlan MixedPlan() => new(
    [
        Row(ArchiveState.Have, "have.7z"), Row(ArchiveState.GameFile, "game.bsa"),
        Row(ArchiveState.Automatic, "auto1.7z"), Row(ArchiveState.Automatic, "auto2.7z"), Row(ArchiveState.Automatic, "auto3.7z"),
        Row(ArchiveState.Browser, "nexus.7z"),
        Row(ArchiveState.Manual, "hand.7z"), Row(ArchiveState.Unsupported, "odd.7z"),
    ]);

    [Fact]
    public async Task StartBuildsThePlanStartsTheRunAndReturnsTheCountsWithoutWaitingForTheDownloads()
    {
        _planner.Plan = MixedPlan();
        _acquirer.RunGate = new TaskCompletionSource();

        var started = await _downloads.Start(Skyrim, CancellationToken.None);

        started.AlreadyRunning.Should().BeFalse();
        started.Modlist.Should().Be(Skyrim);
        started.Counts.Should().Be(new PlanCounts(Have: 2, Automatic: 3, Browser: 1, Manual: 2));
        _planner.Built.Should().ContainSingle().Which.Should().Be((Skyrim.Id, null));
        _acquirer.Runs.Should().ContainSingle().Which.Modlist.Should().Be(Skyrim.Id);
    }

    [Fact]
    public async Task TheRunDoesNotDieWithTheCommandThatStartedIt()
    {
        _acquirer.RunGate = new TaskCompletionSource();
        using var command = new CancellationTokenSource();

        await _downloads.Start(Skyrim, command.Token);
        await command.CancelAsync();

        _acquirer.RunTokens.Should().ContainSingle().Which.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task AskingAgainForTheListThatIsRunningSaysSoAndStartsNothing()
    {
        _acquirer.Status = new AcquireStatus(Skyrim.Id, true, 1, 3, 0, TimeSpan.Zero, []);
        _planner.Plan = MixedPlan();
        _acquirer.Show(MixedPlan() with { Modlist = Skyrim.Id });

        var started = await _downloads.Start(Skyrim, CancellationToken.None);

        started.AlreadyRunning.Should().BeTrue();
        started.Counts.Automatic.Should().Be(3);
        _acquirer.Runs.Should().BeEmpty();
        _planner.Built.Should().BeEmpty();
    }

    [Fact]
    public async Task AnotherListWhileOneIsRunningIsRefusedByName()
    {
        _acquirer.Status = new AcquireStatus(Skyrim.Id, true, 1, 3, 0, TimeSpan.Zero, []);

        var act = () => _downloads.Start(Fallout, CancellationToken.None);

        var refused = await act.Should().ThrowAsync<DownloadBusyException>();
        refused.Which.Message.Should().Contain("Skyrim list").And.Contain("Cancel");
        _acquirer.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task TwoStartsAtTheSameTimeStartOneRun()
    {
        _planner.Gate = new TaskCompletionSource();
        var first = _downloads.Start(Skyrim, CancellationToken.None);

        var second = await _downloads.Start(Skyrim, CancellationToken.None);

        second.AlreadyRunning.Should().BeTrue();
        _planner.Gate.SetResult();
        (await first).AlreadyRunning.Should().BeFalse();
        _acquirer.Runs.Should().HaveCount(1);
    }

    [Fact]
    public async Task APlanThatCannotBeBuiltLeavesNothingRunningAndTheNextStartWorks()
    {
        _planner.Failure = new ModlistGameMissingException("The game is not installed");

        await _downloads.Invoking(d => d.Start(Skyrim, CancellationToken.None)).Should().ThrowAsync<ModlistGameMissingException>();
        _planner.Failure = null;
        var started = await _downloads.Start(Skyrim, CancellationToken.None);

        started.AlreadyRunning.Should().BeFalse();
    }

    [Fact]
    public async Task ARunThatFailsIsLoggedAndDoesNotCrashTheApp()
    {
        _acquirer.RunFailure = new InvalidOperationException("boom");

        await _downloads.Start(Skyrim, CancellationToken.None);

        await WaitForLog(LogLevel.Warning);
        _log.Messages(LogLevel.Warning).Should().ContainSingle().Which.Should().Contain("Skyrim list").And.Contain("boom");
    }

    private async Task WaitForLog(LogLevel level)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (_log.Messages(level).Length == 0 && DateTime.UtcNow < deadline) await Task.Delay(5, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ResumeRunsTheListTheLastSessionLeftUnfinishedAndSaysSo()
    {
        PendingDownload.Set(_settings, Skyrim.Id);
        _planner.Plan = MixedPlan();

        await _downloads.Resume(CancellationToken.None);

        _log.Messages(LogLevel.Information).Should().Contain("Resuming the download of 'Skyrim list' after a restart");
        _planner.Built.Should().ContainSingle().Which.Should().Be((Skyrim.Id, null));
        _acquirer.Runs.Should().ContainSingle().Which.Modlist.Should().Be(Skyrim.Id);
    }

    [Fact]
    public async Task ResumeDoesNothingWhenNoRunWasLeftUnfinished()
    {
        await _downloads.Resume(CancellationToken.None);

        _acquirer.Runs.Should().BeEmpty();
        _planner.Built.Should().BeEmpty();
        _log.Messages(LogLevel.Information).Should().BeEmpty();
    }

    [Fact]
    public async Task ResumeForgetsAListThatIsNoLongerInTheLibrary()
    {
        PendingDownload.Set(_settings, FakeCatalog.Info(0xDEAD, "Gone").Id);

        await _downloads.Resume(CancellationToken.None);

        _acquirer.Runs.Should().BeEmpty();
        PendingDownload.Read(_settings).Should().BeNull();
        _log.Messages(LogLevel.Warning).Should().ContainSingle();
    }

    [Fact]
    public async Task ResumeGivesUpOnAListItCannotPlanAndDoesNotTryAgainNextTime()
    {
        PendingDownload.Set(_settings, Skyrim.Id);
        _planner.Failure = new ModlistGameMissingException("The game is not installed");

        await _downloads.Resume(CancellationToken.None);

        _acquirer.Runs.Should().BeEmpty();
        PendingDownload.Read(_settings).Should().BeNull();
        _log.Messages(LogLevel.Warning).Should().ContainSingle().Which.Should().Contain("Skyrim list").And.Contain("not installed");
    }

    [Fact]
    public async Task ResumeCutShortByTheAppClosingKeepsTheFlag()
    {
        PendingDownload.Set(_settings, Skyrim.Id);
        using var stopping = new CancellationTokenSource();
        _planner.Gate = new TaskCompletionSource();

        var resume = _downloads.Resume(stopping.Token);
        await stopping.CancelAsync();
        _planner.Failure = new OperationCanceledException(stopping.Token);
        _planner.Gate.SetResult();
        await resume;

        PendingDownload.Read(_settings).Should().Be(Skyrim.Id);
    }

    [Fact]
    public async Task CancelStopsTheRunningDownload()
    {
        await _downloads.Start(Skyrim, CancellationToken.None);

        _downloads.Cancel();

        _acquirer.Cancels.Should().Be(1);
    }
}
