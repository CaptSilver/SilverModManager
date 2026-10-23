using FluentAssertions;
using SilverModManager.Platform.Proton.Steam;

namespace SilverModManager.Platform.Proton.Tests.Steam;

public class SteamCloseAndWriteTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A Steam that exits after a number of polls, and a log of everything the sequence did to it, in order.
    /// </summary>
    private sealed class FakeSteam(bool running, int pollsUntilExit = 1)
    {
        public List<string> Log { get; } = [];
        public List<bool> PolledOnContext { get; } = [];
        public SynchronizationContext? Context { get; set; }

        // How many polls Steam's helper processes outlive the main process by; -1 keeps them forever.
        public int HelperPollsAfterExit { get; set; }
        private int _exitedAtPoll = -1;
        public CancellationTokenSource? CancelOnPoll { get; set; }
        public bool Running { get; private set; } = running;
        public int Polls { get; private set; }
        public int NeverExitsAfterRequest { get; set; }

        public bool IsRunning()
        {
            if (Context is not null) PolledOnContext.Add(SynchronizationContext.Current == Context);
            return Running;
        }

        public bool HelpersRunning() =>
            _exitedAtPoll >= 0 && (HelperPollsAfterExit < 0 || Polls - _exitedAtPoll < HelperPollsAfterExit);

        public void RequestExit()
        {
            Log.Add("exit");
            if (NeverExitsAfterRequest == 0) _exitRequested = true;
        }

        public void Start()
        {
            Log.Add(HelpersRunning() ? "start with helpers left" : "start");
            Running = true;
        }

        public Task Delay(TimeSpan _, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Polls++;
            CancelOnPoll?.Cancel();
            if (_exitRequested && Polls >= pollsUntilExit && Running)
            {
                Running = false;
                _exitedAtPoll = Polls;
            }
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        private bool _exitRequested;
    }

    private static SteamCloseAndWrite Sequence(FakeSteam steam) =>
        new(steam.IsRunning, steam.HelpersRunning, steam.RequestExit, steam.Start, steam.Delay, Poll, Timeout, HelperTimeout);

    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Test_AsksSteamToExitWaitsWritesThenStartsSteam()
    {
        var steam = new FakeSteam(running: true, pollsUntilExit: 3);

        var result = await Sequence(steam).RunAsync(() =>
        {
            steam.Running.Should().BeFalse("the write must wait for Steam to be gone");
            steam.Log.Add("write");
            return Task.FromResult("written");
        }, restartSteam: _ => true, CancellationToken.None);

        steam.Log.Should().Equal("exit", "write", "start");
        result.Outcome.Should().Be(SteamCloseOutcome.Written);
        result.Value.Should().Be("written");
        result.SteamRestarted.Should().BeTrue();
        steam.Polls.Should().Be(3);
    }

    [Fact]
    public async Task Test_TimingOutWritesNothingAndLeavesSteamAlone()
    {
        var steam = new FakeSteam(running: true) { NeverExitsAfterRequest = 1 };

        var result = await Sequence(steam).RunAsync(() =>
        {
            steam.Log.Add("write");
            return Task.FromResult("written");
        }, restartSteam: _ => true, CancellationToken.None);

        steam.Log.Should().Equal("exit");
        result.Outcome.Should().Be(SteamCloseOutcome.TimedOut);
        result.Value.Should().BeNull();
        result.SteamRestarted.Should().BeFalse();
        steam.Polls.Should().Be((int)(Timeout / Poll));
    }

    [Fact]
    public async Task Test_CancellingWhileWaitingWritesNothingAndDoesNotStartSteam()
    {
        using var cts = new CancellationTokenSource();
        var steam = new FakeSteam(running: true) { NeverExitsAfterRequest = 1, CancelOnPoll = cts };

        var result = await Sequence(steam).RunAsync(() =>
        {
            steam.Log.Add("write");
            return Task.FromResult("written");
        }, restartSteam: _ => true, cts.Token);

        steam.Log.Should().Equal("exit");
        result.Outcome.Should().Be(SteamCloseOutcome.Cancelled);
        result.SteamRestarted.Should().BeFalse();
    }

    [Fact]
    public async Task Test_SteamAlreadyClosedSkipsTheExitRequestAndTheRestart()
    {
        var steam = new FakeSteam(running: false);

        var result = await Sequence(steam).RunAsync(() =>
        {
            steam.Log.Add("write");
            return Task.FromResult("written");
        }, restartSteam: _ => true, CancellationToken.None);

        steam.Log.Should().Equal("write");
        result.Outcome.Should().Be(SteamCloseOutcome.Written);
        result.SteamRestarted.Should().BeFalse();
    }

    [Fact]
    public async Task Test_SteamStaysClosedWhenTheCallerAsksForThat()
    {
        var steam = new FakeSteam(running: true);

        var result = await Sequence(steam).RunAsync(() => Task.FromResult("needs a second write"), restartSteam: _ => false, CancellationToken.None);

        steam.Log.Should().Equal("exit");
        result.Outcome.Should().Be(SteamCloseOutcome.Written);
        result.SteamRestarted.Should().BeFalse();
    }

    [Fact]
    public async Task Test_SteamIsStartedAgainWhenTheWriteThrows()
    {
        var steam = new FakeSteam(running: true);

        var act = async () => await Sequence(steam).RunAsync<string>(() => throw new IOException("disk full"), restartSteam: _ => true, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        steam.Log.Should().Equal("exit", "start");
    }

    [Fact]
    public async Task Test_ReportsWaitingOnlyWhileItWaitsForSteam()
    {
        var steam = new FakeSteam(running: true);
        var waiting = new List<string>();

        await Sequence(steam).RunAsync(() => { waiting.Add("write"); return Task.FromResult(1); }, restartSteam: _ => true, CancellationToken.None, onWaiting: () => waiting.Add("waiting"));
        await Sequence(new FakeSteam(running: false)).RunAsync(() => { waiting.Add("write"); return Task.FromResult(1); }, restartSteam: _ => true, CancellationToken.None, onWaiting: () => waiting.Add("waiting"));

        waiting.Should().Equal("waiting", "write", "write");
    }

    [Fact]
    public async Task Test_StartsSteamAgainOnlyOnceItsHelpersHaveGone()
    {
        // A new Steam shares the old one's browser cache, which a webhelper still shutting down keeps locked.
        var steam = new FakeSteam(running: true) { HelperPollsAfterExit = 3 };

        await Sequence(steam).RunAsync(() => Task.FromResult(1), restartSteam: _ => true, CancellationToken.None);

        steam.Log.Should().Equal("exit", "start");
        steam.Polls.Should().Be(1 + 3);
    }

    [Fact]
    public async Task Test_StartsSteamAnywayWhenItsHelpersLinger()
    {
        var steam = new FakeSteam(running: true) { HelperPollsAfterExit = -1 };

        await Sequence(steam).RunAsync(() => Task.FromResult(1), restartSteam: _ => true, CancellationToken.None);

        steam.Log.Should().Equal("exit", "start with helpers left");
        steam.Polls.Should().Be(1 + (int)(HelperTimeout / Poll));
    }

    [Fact]
    public async Task Test_AFailedWriteAlsoWaitsForTheHelpersBeforeStartingSteam()
    {
        var steam = new FakeSteam(running: true) { HelperPollsAfterExit = 2 };

        var act = async () => await Sequence(steam).RunAsync<string>(() => throw new IOException("disk full"), restartSteam: _ => true, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        steam.Log.Should().Equal("exit", "start");
    }

    [Fact]
    public async Task Test_PollsSteamOffTheCallersThread()
    {
        // The caller is the dialog; scanning /proc on its context would stall the UI every poll.
        var context = new PoolContext();
        var steam = new FakeSteam(running: true, pollsUntilExit: 2) { Context = context };
        var sequence = new SteamCloseAndWrite(steam.IsRunning, steam.HelpersRunning, steam.RequestExit, steam.Start,
            async (span, token) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1), token);
                await steam.Delay(span, token);
            }, Poll, Timeout, HelperTimeout);

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        Task<SteamCloseAndWriteResult<int>> run;
        try { run = sequence.RunAsync(() => Task.FromResult(1), restartSteam: _ => false, CancellationToken.None); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await run;

        steam.PolledOnContext.Should().HaveCountGreaterThan(1);
        steam.PolledOnContext.Skip(1).Should().AllBeEquivalentTo(false);
    }

    /// <summary>
    /// Runs posted work on the pool with itself installed, so code that resumes on it can be told apart.
    /// </summary>
    private sealed class PoolContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => ThreadPool.QueueUserWorkItem(_ =>
        {
            SetSynchronizationContext(this);
            d(state);
        });
    }
}
