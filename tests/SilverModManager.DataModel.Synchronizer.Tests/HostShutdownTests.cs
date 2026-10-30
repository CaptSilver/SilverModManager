using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// The test base used to start the host's shutdown and return, so the next test ran (and the process could exit)
/// while the databases were still closing. That is how a query still in flight met a closed RocksDB.
/// </summary>
public class HostShutdownTests
{
    [ThreadStatic] private static Gate? _gate;

    private sealed class Gate : IDisposable
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new();

        public void Dispose()
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(30));
        }
    }

    private sealed class Probe(ITestOutputHelper helper) : AStubbedGameTest<Probe>(helper)
    {
        // A factory, because the container disposes only what it created itself, not an instance handed to it.
        protected override IServiceCollection AddServices(IServiceCollection services)
        {
            var gate = _gate!;
            return base.AddServices(services).AddSingleton<Gate>(_ => gate);
        }

        public void Touch() => ServiceProvider.GetRequiredService<Gate>();
    }

    [Fact]
    public async Task DisposeAsyncReturnsOnlyOnceTheHostHasShutDown()
    {
        var gate = _gate = new Gate();
        var probe = new Probe(TestContext.Current.TestOutputHelper!);
        _gate = null;
        probe.Touch();

        var dispose = Task.Run(async () => await probe.DisposeAsync());
        gate.Entered.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("the host disposes its singletons");
        (await Task.WhenAny(dispose, Task.Delay(500))).Should().NotBeSameAs(dispose,
            "DisposeAsync returned while the host was still shutting down");

        gate.Release.Set();
        await dispose.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
