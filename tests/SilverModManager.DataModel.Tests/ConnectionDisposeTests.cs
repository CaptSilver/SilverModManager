using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.MnemonicDB;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Storage;
using SilverModManager.MnemonicDB.Storage.Abstractions;
using RocksDbBackend = SilverModManager.MnemonicDB.Storage.RocksDbBackend.Backend;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// The connection's event thread runs subscribers and reads the pending-event queue. Dispose frees that queue,
/// so returning while the thread is still working leaves it touching a disposed collection, and the resulting
/// unhandled exception takes the whole process down.
/// </summary>
public class ConnectionDisposeTests
{
    private static ServiceProvider BuildProvider() => new ServiceCollection()
        .AddLogging()
        .AddMnemonicDB()
        .AddDatomStoreSettings(new DatomStoreSettings())
        .AddSingleton<IStoreBackend, RocksDbBackend>()
        .BuildServiceProvider();

    [Fact]
    public async Task DisposeWaitsForTheEventThreadToFinishItsCurrentEvent()
    {
        await using var provider = BuildProvider();
        var connection = provider.GetRequiredService<IConnection>();

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var armed = false;
        using var subscription = connection.Revisions.Subscribe(_ =>
        {
            if (!armed) return;
            entered.Set();
            release.Wait();
        }, _ => { });
        armed = true;

        Exception? unhandled = null;
        UnhandledExceptionEventHandler handler = (_, e) => unhandled = e.ExceptionObject as Exception;
        AppDomain.CurrentDomain.UnhandledException += handler;
        try
        {
            var commit = Task.Run(async () =>
            {
                try { await connection.BeginTransaction().Commit(); }
                catch (Exception) { /* the connection is shutting down underneath it */ }
            });
            entered.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("the event thread must reach the subscriber");

            var dispose = Task.Run(((IDisposable)connection).Dispose);
            (await Task.WhenAny(dispose, Task.Delay(300))).Should().NotBeSameAs(dispose,
                "Dispose returned while the event thread was still handling an event");

            release.Set();
            await dispose.WaitAsync(TimeSpan.FromSeconds(30));
            await commit.WaitAsync(TimeSpan.FromSeconds(30));

            // Give a thread that outlived Dispose time to fault on the freed queue.
            await Task.Delay(300);
            unhandled.Should().BeNull();
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= handler;
        }
    }

    [Fact]
    public async Task DisposeFromTheEventThreadDoesNotWaitForItself()
    {
        await using var provider = BuildProvider();
        var connection = provider.GetRequiredService<IConnection>();

        using var disposed = new ManualResetEventSlim();
        var armed = false;
        using var subscription = connection.Revisions.Subscribe(_ =>
        {
            if (!armed) return;
            ((IDisposable)connection).Dispose();
            disposed.Set();
        }, _ => { });
        armed = true;

        var commit = Task.Run(async () =>
        {
            try { await connection.BeginTransaction().Commit(); }
            catch (Exception) { /* the connection is shutting down underneath it */ }
        });

        disposed.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("Dispose from a subscriber must return");
        await commit.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
