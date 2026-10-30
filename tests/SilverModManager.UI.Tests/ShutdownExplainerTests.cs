using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SilverModManager.App;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.UI.Tests;

public class ShutdownExplainerTests
{
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }

        public string[] Info() { lock (Entries) return Entries.Where(e => e.Level == LogLevel.Information).Select(e => e.Message).ToArray(); }
    }

    private sealed record SampleDownload : IJobDefinition;

    private sealed class FakeHost : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public int StopRequests;
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { StopRequests++; _stopping.Cancel(); }
    }

    private static IJobMonitor Monitor(params (JobStatus Status, IJobDefinition Definition)[] jobs)
    {
        var list = jobs.Select(j =>
        {
            var job = Substitute.For<IJob>();
            job.Status.Returns(j.Status);
            job.Definition.Returns(j.Definition);
            job.Progress.Returns(Optional<Percent>.None);
            return job;
        }).ToList();
        var monitor = Substitute.For<IJobMonitor>();
        monitor.Jobs.Returns(new ReadOnlyObservableCollection<IJob>(new ObservableCollection<IJob>(list)));
        return monitor;
    }

    private static (ShutdownExplainer Explainer, CapturingLogger<ShutdownExplainer> Log, FakeHost Host) Create(IJobMonitor? monitor = null)
    {
        var log = new CapturingLogger<ShutdownExplainer>();
        var host = new FakeHost();
        return (new ShutdownExplainer(host, monitor ?? Monitor(), log), log, host);
    }

    [Theory]
    [InlineData(PosixSignal.SIGTERM)]
    [InlineData(PosixSignal.SIGINT)]
    [InlineData(PosixSignal.SIGQUIT)]
    public void Test_ASignalIsLoggedByNameAndLeftForTheHostToHandle(PosixSignal signal)
    {
        var (explainer, log, host) = Create();
        var context = new PosixSignalContext(signal);

        explainer.OnSignal(context);

        log.Info().Should().ContainSingle(m => m.Contains(signal.ToString()));
        context.Cancel.Should().BeFalse("the console lifetime owns these signals");
        host.StopRequests.Should().Be(0);
    }

    [Fact]
    public void Test_AHangupStopsAWindowedAppGracefullyInsteadOfKillingIt()
    {
        var (explainer, log, host) = Create();
        explainer.Attach(Substitute.For<IClassicDesktopStyleApplicationLifetime>());
        var context = new PosixSignalContext(PosixSignal.SIGHUP);

        explainer.OnSignal(context);

        context.Cancel.Should().BeTrue();
        host.StopRequests.Should().Be(1);
        log.Info().Should().Contain(m => m.Contains("SIGHUP"));
    }

    [Fact]
    public void Test_AHangupIsOnlyLoggedWhenNothingOwnsTheWindow()
    {
        var (explainer, log, host) = Create();
        var context = new PosixSignalContext(PosixSignal.SIGHUP);

        explainer.OnSignal(context);

        context.Cancel.Should().BeFalse();
        host.StopRequests.Should().Be(0);
        log.Info().Should().Contain(m => m.Contains("SIGHUP"));
    }

    [Fact]
    public async Task Test_AStopFromCodeNamesTheCaller()
    {
        var builder = Host.CreateApplicationBuilder();
        using var host = builder.Build();
        var log = new CapturingLogger<ShutdownExplainer>();
        var explainer = new ShutdownExplainer(host.Services.GetRequiredService<IHostApplicationLifetime>(), Monitor(), log);
        await explainer.StartAsync(CancellationToken.None);

        StopTheHostFromANamedMethod(host);

        log.Info().Should().Contain(m => m.Contains(nameof(StopTheHostFromANamedMethod)));
        await explainer.StopAsync(CancellationToken.None);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StopTheHostFromANamedMethod(IHost host) => host.StopAsync().GetAwaiter().GetResult();

    [Fact]
    public void Test_ANotedReasonWinsOverTheCallerGuess()
    {
        var (explainer, log, host) = Create();
        explainer.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        explainer.Note("the updater restarts the app");
        host.StopApplication();

        log.Info().Should().Contain(m => m.Contains("the updater restarts the app"));
    }

    [Fact]
    public void Test_ActiveJobsAreListedWhenTheStopBegins()
    {
        var monitor = Monitor((JobStatus.Running, new SampleDownload()), (JobStatus.Completed, new SampleDownload()), (JobStatus.Paused, new SampleDownload()));
        var (explainer, log, host) = Create(monitor);
        explainer.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        host.StopApplication();

        log.Info().Should().ContainSingle(m => m.Contains("2 active job") && m.Contains(nameof(SampleDownload)) && m.Contains("Running") && m.Contains("Paused"));
    }

    [Fact]
    public void Test_NoJobLineWhenNothingIsRunning()
    {
        var (explainer, log, host) = Create(Monitor((JobStatus.Completed, new SampleDownload())));
        explainer.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        host.StopApplication();

        log.Info().Should().NotContain(m => m.Contains("active job"));
    }

    [Theory]
    [InlineData(WindowCloseReason.WindowClosing, false, "closed by the user")]
    [InlineData(WindowCloseReason.OSShutdown, false, "operating system")]
    [InlineData(WindowCloseReason.ApplicationShutdown, false, "application shutdown")]
    [InlineData(WindowCloseReason.Undefined, true, "by the application")]
    public void Test_WindowCloseReasonsReadInPlainWords(WindowCloseReason reason, bool programmatic, string expected)
    {
        ShutdownExplainer.DescribeWindowClose(reason, programmatic).Should().Contain(expected);
    }

    [Fact]
    public void Test_WindowedAndHeadlessMainsRegisterItAsAHostedService()
    {
        static IServiceCollection Graph(bool showUi, bool headlessRun) => new ServiceCollection().AddLogging().AddApp(startupMode: new StartupMode
        {
            RunAsMain = true,
            ShowUI = showUi,
            ExecuteCli = false,
            IsHeadlessRun = headlessRun,
        });

        Graph(showUi: true, headlessRun: false).Should().ContainSingle(d => d.ServiceType == typeof(ShutdownExplainer));
        Graph(showUi: false, headlessRun: true).Should().ContainSingle(d => d.ServiceType == typeof(ShutdownExplainer));
    }
}
