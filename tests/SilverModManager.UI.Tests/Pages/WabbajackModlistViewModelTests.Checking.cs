using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.App.UI.Windows;
using SilverModManager.Sdk.Games;
using SilverModManager.Wabbajack.Acquire;

namespace SilverModManager.UI.Tests.Pages;

// What the page says while it checks a list, and that the check never holds the page still.
public partial class WabbajackModlistViewModelTests
{
    private static async Task UntilPlanning(FakeService service)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (service.LastProgress is null) await Task.Delay(10, cts.Token);
    }

    [Fact]
    public async Task WhileTheListIsChecked_ThePageSaysSoAtOnceAndFollowsEachPhase()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Have)), load: false);
        var gate = new TaskCompletionSource();
        service.PlanGate = gate;

        var loading = vm.LoadAsync(CancellationToken.None);
        await UntilPlanning(service);

        vm.IsPlanning.Should().BeTrue();
        vm.CheckText.Should().NotBeEmpty("the page is never silent while it works");
        vm.IsCheckIndeterminate.Should().BeTrue("nothing has been counted yet");

        service.LastProgress!.Report(new PlanProgress(PlanPhase.CheckingLibrary, 412, 1294));
        vm.CheckText.Should().Be("Checking archives against your library: 412 of 1294");
        vm.IsCheckIndeterminate.Should().BeFalse();
        vm.CheckFraction.Should().BeApproximately(412d / 1294, 1e-9);

        service.LastProgress.Report(new PlanProgress(PlanPhase.HashingGameFiles, 2_100_000_000, 8_400_000_000));
        vm.CheckText.Should().Be("Hashing game files: 2.1 of 8.4 GB");
        vm.CheckFraction.Should().BeApproximately(0.25, 1e-9);

        gate.SetResult();
        await loading;

        vm.CheckText.Should().BeEmpty();
        vm.IsCheckIndeterminate.Should().BeFalse();
        vm.IsPlanning.Should().BeFalse();
    }

    [Fact]
    public async Task AFailedCheck_ShowsItsReasonAndNoProgress()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Have)), load: false);
        service.PlanFailure = new InvalidOperationException("The game is not installed");

        await vm.LoadAsync(CancellationToken.None);

        vm.ErrorText.Should().Contain("The game is not installed");
        vm.CheckText.Should().BeEmpty();
        vm.IsPlanning.Should().BeFalse();
    }

    [Fact]
    public async Task TheCheckRunsOffTheUiThread()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Have)), load: false);

        await Dispatcher.UIThread.InvokeAsync(() => vm.LoadAsync(CancellationToken.None));

        service.PlannedOnUiThread.Should().BeFalse("hashing and lookups on the UI thread freeze the whole window");
    }

    [Fact]
    public async Task APickThatIsReplacedWhileItsCheckRuns_NeverOverwritesTheNewerPick()
    {
        var (vm, service, acquirer) = await Open(Plan(Row(ArchiveState.Manual, "old.7z")), game: GameId.From("CreationEngine.NoSuchGame"), load: false);
        var gate = new TaskCompletionSource();
        service.PlanGate = gate;
        var first = vm.LoadAsync(CancellationToken.None);
        await UntilPlanning(service);

        service.Plan = Plan(Row(ArchiveState.Have, "new.7z"));
        await vm.SelectLoadoutAsync(WabbajackLoadoutChoice.CreateNew, CancellationToken.None);
        gate.SetResult();
        await first;

        vm.Archives.Should().ContainSingle().Which.Name.Should().Be("new.7z", "the first check finished late and belongs to a pick that is gone");
        acquirer.Shown.Should().ContainSingle().Which.Rows.Single().Archive.Name.Should().Be("new.7z");
        vm.IsPlanning.Should().BeFalse();
        vm.ErrorText.Should().BeEmpty("a replaced check is not a failure");
        vm.CheckText.Should().BeEmpty();
    }

    [Fact]
    public async Task APickThatIsReplaced_DoesNotEndThePlanningOfTheNewerPick()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Have)), game: GameId.From("CreationEngine.NoSuchGame"), load: false);
        var firstGate = new TaskCompletionSource();
        service.PlanGate = firstGate;
        var first = vm.LoadAsync(CancellationToken.None);
        await UntilPlanning(service);

        var secondGate = new TaskCompletionSource();
        service.PlanGate = secondGate;
        var second = vm.SelectLoadoutAsync(WabbajackLoadoutChoice.CreateNew, CancellationToken.None);
        firstGate.SetResult();
        await first;

        vm.IsPlanning.Should().BeTrue("the newer pick is still being checked");
        vm.CanInstall.Should().BeFalse();

        secondGate.SetResult();
        await second;
        vm.IsPlanning.Should().BeFalse();
    }

    private sealed class CapturingLogger : ILogger<WabbajackModlistViewModel>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception) + (exception is null ? "" : " | " + exception.Message)));
        }
    }

    private sealed class LoggingProvider(IServiceProvider inner, ILogger<WabbajackModlistViewModel> logger) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(ILogger<WabbajackModlistViewModel>) ? logger : inner.GetService(serviceType);
    }

    private WabbajackModlistViewModel WithLogger(FakeService service, FakeAcquirer acquirer, CapturingLogger logger)
        => new(Provider.GetRequiredService<IWindowManager>(), Connection, service, acquirer, new LoggingProvider(Provider, logger)) { FileId = SomeFile };

    [Fact]
    public async Task TheCheckIsLoggedAtInfo_FromStartThroughPhasesToTheCounts()
    {
        await SeedLoadoutRow(GameStore.Steam, "Wabbajack target");
        var service = new FakeService(TestGame) { Plan = Plan(Row(ArchiveState.Have, "a.7z"), Row(ArchiveState.Manual, "b.7z"), Row(ArchiveState.Browser, "c.7z")) };
        var logger = new CapturingLogger();
        var vm = WithLogger(service, new FakeAcquirer(), logger);
        var gate = new TaskCompletionSource();
        service.PlanGate = gate;

        var loading = vm.LoadAsync(CancellationToken.None);
        await UntilPlanning(service);
        service.LastProgress!.Report(new PlanProgress(PlanPhase.CheckingLibrary, 1, 3));
        service.LastProgress.Report(new PlanProgress(PlanPhase.CheckingLibrary, 2, 3));
        service.LastProgress.Report(new PlanProgress(PlanPhase.HashingGameFiles, 0, 100, "x.bsa"));
        gate.SetResult();
        await loading;

        var info = logger.Entries.Where(entry => entry.Level == LogLevel.Information).Select(entry => entry.Message).ToArray();
        info.Should().Contain(message => message.Contains("Test list") && message.Contains("2 archives") && message.Contains("Checking"), "the start names the list and its archive count");
        info.Count(message => message.Contains("Checking archives against your library")).Should().Be(1, "a phase is logged when it begins, not on every count");
        info.Should().Contain(message => message.Contains("Hashing game files"));
        info.Should().Contain(message => message.Contains("1 ready") && message.Contains("1 to get") && message.Contains("1 by hand") && message.Contains("0 game files differ"));
        info.Last().Should().MatchRegex(@"\d+(\.\d+)? s", "the end gives the duration");
    }

    [Fact]
    public async Task AFailedCheckIsLoggedWithItsReason()
    {
        await SeedLoadoutRow(GameStore.Steam, "Wabbajack target");
        var service = new FakeService(TestGame) { Plan = Plan(Row(ArchiveState.Have)), PlanFailure = new InvalidOperationException("disk gone") };
        var logger = new CapturingLogger();
        var vm = WithLogger(service, new FakeAcquirer(), logger);

        await vm.LoadAsync(CancellationToken.None);

        logger.Entries.Should().Contain(entry => entry.Level >= LogLevel.Warning && entry.Message.Contains("Test list") && entry.Message.Contains("disk gone"));
    }
}
