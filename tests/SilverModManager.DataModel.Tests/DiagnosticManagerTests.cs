using System.Collections.Concurrent;
using FluentAssertions;
using System.Collections.Frozen;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.DataModel.Tests;

public class DiagnosticManagerTests(IServiceProvider provider) : AGameTest<Cyberpunk2077Game>(provider)
{
    [Fact]
    public async Task DiagnosticManager_RunsEmittersRegisteredThroughDependencyInjection()
    {
        var recorder = provider.GetServices<IDiagnosticEmitter>().OfType<RecordingLoadoutEmitter>().Should().ContainSingle().Subject;
        var loadout = await CreateLoadout();

        await DiagnosticManager.GetLoadoutDiagnostics(loadout.LoadoutId).Timeout(TimeSpan.FromSeconds(30)).FirstAsync();

        recorder.Loadouts.Should().Contain(loadout.LoadoutId);
    }

    [Fact]
    public async Task RefreshAsync_RerunsEmittersOnAnUnchangedLoadoutAndPublishesTheNewAnswer()
    {
        var emitter = ScriptedEmitter();
        var loadout = await CreateLoadout();
        var script = emitter.Scripts.GetOrAdd(loadout.LoadoutId, _ => new EmitterScript { Answer = "before the fix" });

        var first = await DiagnosticManager.GetLoadoutDiagnostics(loadout.LoadoutId).Timeout(TimeSpan.FromSeconds(30)).FirstAsync();
        first.Select(d => d.Title).Should().Equal("before the fix");

        // Nothing in the database changes, only what the emitter would find.
        script.Answer = "after the fix";
        await DiagnosticManager.RefreshAsync(loadout.LoadoutId).WaitAsync(TimeSpan.FromSeconds(30));

        var second = await DiagnosticManager.GetLoadoutDiagnostics(loadout.LoadoutId).Timeout(TimeSpan.FromSeconds(30)).FirstAsync();
        second.Select(d => d.Title).Should().Equal("after the fix");
        script.Started.Should().Be(2);
    }

    [Fact]
    public async Task RefreshAsync_WhileARefreshIsRunningJoinsItInsteadOfStartingAnother()
    {
        var emitter = ScriptedEmitter();
        var loadout = await CreateLoadout();
        var script = emitter.Scripts.GetOrAdd(loadout.LoadoutId, _ => new EmitterScript { Answer = "answer" });
        await DiagnosticManager.GetLoadoutDiagnostics(loadout.LoadoutId).Timeout(TimeSpan.FromSeconds(30)).FirstAsync();
        script.Started.Should().Be(1);

        // Let the revision debounce settle so only the refresh can start a run.
        await Task.Delay(600);
        var startedBeforeRefresh = script.Started;

        script.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRefresh = DiagnosticManager.RefreshAsync(loadout.LoadoutId);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (script.Started <= startedBeforeRefresh)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the refresh should have started a run");
            await Task.Delay(10);
        }

        var secondRefresh = DiagnosticManager.RefreshAsync(loadout.LoadoutId);
        await Task.Delay(250);

        script.Started.Should().Be(startedBeforeRefresh + 1, "the second call joins the run that is already in flight");
        firstRefresh.IsCompleted.Should().BeFalse();
        secondRefresh.IsCompleted.Should().BeFalse();

        script.Gate.SetResult();
        await Task.WhenAll(firstRefresh, secondRefresh).WaitAsync(TimeSpan.FromSeconds(30));
        script.Started.Should().Be(startedBeforeRefresh + 1);
    }

    [Fact]
    public async Task RefreshAsync_RequestedWhileAResultIsBeingPublished_LeavesTheManagerUsable()
    {
        var emitter = ScriptedEmitter();
        var loadout = await CreateLoadout();
        emitter.Scripts.GetOrAdd(loadout.LoadoutId, _ => new EmitterScript { Answer = "answer" });
        await DiagnosticManager.GetLoadoutDiagnostics(loadout.LoadoutId).Timeout(TimeSpan.FromSeconds(30)).FirstAsync();
        await Task.Delay(600);

        // A subscriber still handling a published result keeps the pipeline busy. A refresh asked for from
        // another thread in that window must not shut everyone else out of the manager while it waits.
        var lookupReturned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? secondRefresh = null;
        using var subscription = DiagnosticManager.GetLoadoutDiagnostics(loadout.LoadoutId)
            .Skip(1)
            .Take(1)
            .Subscribe(_ =>
            {
                secondRefresh = Task.Run(() => DiagnosticManager.RefreshAsync(loadout.LoadoutId));
                Thread.Sleep(500);
                var lookup = Task.Run(() => DiagnosticManager.GetLoadoutDiagnostics(loadout.LoadoutId));
                lookupReturned.TrySetResult(lookup.Wait(TimeSpan.FromSeconds(2)));
            });

        await DiagnosticManager.RefreshAsync(loadout.LoadoutId).WaitAsync(TimeSpan.FromSeconds(30));
        (await lookupReturned.Task.WaitAsync(TimeSpan.FromSeconds(30))).Should().BeTrue();
        await secondRefresh!.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task RefreshAsync_CalledFromAResultSubscriber_WhileARevisionIsBeingDebounced_DoesNotDeadlock()
    {
        var emitter = ScriptedEmitter();
        var loadout = await CreateLoadout();
        emitter.Scripts.GetOrAdd(loadout.LoadoutId, _ => new EmitterScript { Answer = "answer" });
        await DiagnosticManager.GetLoadoutDiagnostics(loadout.LoadoutId).Timeout(TimeSpan.FromSeconds(30)).FirstAsync();
        await Task.Delay(600);

        // A subscriber runs on the thread that publishes a result. While it is still running, a database
        // change makes the debounce timer fire and push a revision into the pipeline. The subscriber then
        // asks for a refresh: that must not wait on the revision, which in turn waits on the publisher.
        var refreshReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? subscriberRefresh = null;
        // Not disposed by the test: a failing run would block on the stuck publisher instead of reporting it.
        // Take(1) ends the subscription on its own.
        DiagnosticManager.GetLoadoutDiagnostics(loadout.LoadoutId)
            .Skip(1)
            .Take(1)
            .Subscribe(published =>
            {
                using (var tx = Connection.BeginTransaction())
                {
                    tx.Add(loadout.LoadoutId, Loadout.LastAppliedDateTime, DateTime.UtcNow);
                    tx.Commit().GetAwaiter().GetResult();
                }

                // Long enough for the 250 ms debounce to fire and block on the publisher.
                Thread.Sleep(1500);
                subscriberRefresh = DiagnosticManager.RefreshAsync(loadout.LoadoutId);
                refreshReturned.TrySetResult();
            });

        _ = DiagnosticManager.RefreshAsync(loadout.LoadoutId);
        await refreshReturned.Task.WaitAsync(TimeSpan.FromSeconds(20));

        // The refresh asked for from the subscriber still runs and completes once the subscriber returns.
        await subscriberRefresh!.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Diagnostics_CarryTheirFixActionsThroughTheManager()
    {
        var emitter = ScriptedEmitter();
        var loadout = await CreateLoadout();
        emitter.Scripts.GetOrAdd(loadout.LoadoutId, _ => new EmitterScript
        {
            Answer = "has a fix",
            Actions = [new DiagnosticAction("scripted-fix", "Fix it", ["one", "two"])],
        });

        var diagnostics = await DiagnosticManager.GetLoadoutDiagnostics(loadout.LoadoutId).Timeout(TimeSpan.FromSeconds(30)).FirstAsync();

        var action = diagnostics.Should().ContainSingle().Which.Actions.Should().ContainSingle().Subject;
        action.Id.Should().Be("scripted-fix");
        action.Label.Should().Be("Fix it");
        action.Arguments.Should().Equal("one", "two");
    }

    [Fact]
    public async Task Diagnostics_WithoutAFixHaveNoActions()
    {
        var emitter = ScriptedEmitter();
        var loadout = await CreateLoadout();
        emitter.Scripts.GetOrAdd(loadout.LoadoutId, _ => new EmitterScript { Answer = "no fix" });

        var diagnostics = await DiagnosticManager.GetLoadoutDiagnostics(loadout.LoadoutId).Timeout(TimeSpan.FromSeconds(30)).FirstAsync();

        diagnostics.Should().ContainSingle().Which.Actions.Should().BeEmpty();
    }

    [Fact]
    public void DiagnosticActions_AreEqualWhenTheirIdLabelAndArgumentsAre()
    {
        var a = new DiagnosticAction("id", "Label", ["x", "y"]);

        a.Should().Be(new DiagnosticAction("id", "Label", ["x", "y"]));
        a.GetHashCode().Should().Be(new DiagnosticAction("id", "Label", ["x", "y"]).GetHashCode());
        a.Should().NotBe(new DiagnosticAction("id", "Label", ["x"]));
        a.Should().NotBe(new DiagnosticAction("other", "Label", ["x", "y"]));
    }

    [Fact]
    public void DiagnosticActions_DifferWhenOnlyTheirNoteDoes()
    {
        var a = new DiagnosticAction("id", "Label", ["x"], "Steam is restarted.");

        a.Should().Be(new DiagnosticAction("id", "Label", ["x"], "Steam is restarted."));
        a.GetHashCode().Should().Be(new DiagnosticAction("id", "Label", ["x"], "Steam is restarted.").GetHashCode());
        a.Should().NotBe(new DiagnosticAction("id", "Label", ["x"]));
        a.Should().NotBe(new DiagnosticAction("id", "Label", ["x"], "Something else."));
    }

    [Fact]
    public void TheSteamLaunchOptionAction_HasAStableId()
    {
        var action = DiagnosticActions.UseLoadoutFromSteam();

        action.Id.Should().Be(DiagnosticActions.UseLoadoutFromSteamId);
        action.Id.Should().Be("use-loadout-from-steam");
        action.Label.Should().NotBeNullOrWhiteSpace();
    }

    private ScriptedLoadoutEmitter ScriptedEmitter() =>
        provider.GetServices<IDiagnosticEmitter>().OfType<ScriptedLoadoutEmitter>().Should().ContainSingle().Subject;
}

public sealed class EmitterScript
{
    public required string Answer { get; set; }
    public System.Collections.Immutable.ImmutableArray<DiagnosticAction> Actions { get; init; } = [];
    public TaskCompletionSource? Gate { get; set; }
    private int _started;
    public int Started => Volatile.Read(ref _started);
    public void MarkStarted() => Interlocked.Increment(ref _started);
}

/// <summary>
/// Reports whatever its per-loadout script says at the time it runs, optionally held open until the
/// script's gate is released, so a test can change the answer or watch for overlapping runs.
/// </summary>
public sealed class ScriptedLoadoutEmitter : ILoadoutDiagnosticEmitter
{
    public ConcurrentDictionary<LoadoutId, EmitterScript> Scripts { get; } = new();

    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken) => throw new NotSupportedException();

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (!Scripts.TryGetValue(loadout.LoadoutId, out var script)) yield break;

        script.MarkStarted();
        if (script.Gate is { } gate) await gate.Task.WaitAsync(cancellationToken);

        yield return new Diagnostic
        {
            Id = new DiagnosticId("SilverModManager.DataModel.Tests", 1),
            Title = script.Answer,
            Severity = DiagnosticSeverity.Warning,
            Summary = DiagnosticMessage.From(script.Answer),
            Details = DiagnosticMessage.DefaultValue,
            DataReferences = new Dictionary<DataReferenceDescription, IDataReference>(),
            Actions = script.Actions,
        };
    }
}

/// <summary>
/// Registered in the test graph with <c>TryAddEnumerable</c>, the way a module adds an emitter that
/// applies to every game.
/// </summary>
public sealed class RecordingLoadoutEmitter : ILoadoutDiagnosticEmitter
{
    public ConcurrentBag<LoadoutId> Loadouts { get; } = [];

    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken) => throw new NotSupportedException();

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        Loadouts.Add(loadout.LoadoutId);
        yield break;
    }
}
