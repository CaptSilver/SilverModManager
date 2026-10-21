using System.Collections.Concurrent;
using FluentAssertions;
using System.Collections.Frozen;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.TestFramework;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using Loadout = NexusMods.Sdk.Loadouts.Loadout;

namespace NexusMods.DataModel.Tests;

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
