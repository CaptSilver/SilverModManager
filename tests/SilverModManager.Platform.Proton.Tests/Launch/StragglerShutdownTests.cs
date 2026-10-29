using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Games.TestFramework;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tests.Launch;

public class StragglerShutdownTests
{
    private const int Forwarder = 500;
    private const int ForwarderShell = 400;
    private const int Game = 600;
    private const int SigTerm = 15;
    private const int SigKill = 9;
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(100);

    private static GameInstallation Installation()
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game");
        var folder = FileSystem.Shared.FromUnsanitizedFullPath("/tmp/silvermod-straggler-shutdown");
        var result = new GameLocatorResult
        {
            Game = game,
            Path = folder,
            Store = GameStore.Steam,
            StoreIdentifier = "1",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
        };
        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, folder)));
    }

    // Every process lives until it is sent SIGKILL, and the signals sent are recorded instead of delivered.
    private sealed class Rig
    {
        private readonly HashSet<int> _alive;
        public readonly List<(int Pid, int Signal)> Signals = [];
        public readonly FakeProcessTracker Tracker = new();

        public Rig(params int[] pids)
        {
            _alive = [..pids];
            Tracker.ForInstallation = _ => _alive.Select(pid => new TrackedProcess(pid, "p" + pid)).ToList();
        }

        public int Signal(int pid, int signal)
        {
            lock (Signals) Signals.Add((pid, signal));
            if (signal == SigKill) _alive.Remove(pid);
            return 0;
        }
    }

    [Fact]
    public async Task AGameProcessThatOutlivesItsGraceGetsSigtermThenSigkill()
    {
        var rig = new Rig(Game);

        var survivor = await StragglerShutdown.RunAsync(rig.Tracker, Installation(), Grace, CancellationToken.None, signal: rig.Signal);

        survivor.Should().BeNull();
        rig.Signals.Should().Equal((Game, SigTerm), (Game, SigKill));
    }

    [Fact]
    public async Task TheForwardingClientAndItsAncestorsAreNeverSignalled_WhileTheGameStillIs()
    {
        // The forwarding client carries the game's prefix in its environment, as does the shell above it, so the
        // tracker lists both next to the game. They are waiting to be told the game's exit, not leftovers.
        var rig = new Rig(Forwarder, ForwarderShell, Game);
        IReadOnlySet<int> excluded = new HashSet<int> { Forwarder };

        var survivor = await StragglerShutdown.RunAsync(rig.Tracker, Installation(), Grace, CancellationToken.None,
            excluded, rig.Signal, parentOf: pid => pid == Forwarder ? ForwarderShell : null);

        rig.Signals.Should().NotContain(s => s.Pid == Forwarder || s.Pid == ForwarderShell);
        rig.Signals.Should().Equal((Game, SigTerm), (Game, SigKill));
        survivor.Should().BeNull("the excluded processes are not the game's, so they are not counted as survivors");
    }

    [Fact]
    public void TheParentOfThisProcessIsReadFromProc()
    {
        StragglerShutdown.ParentOf(Environment.ProcessId).Should().BePositive();
    }
}
