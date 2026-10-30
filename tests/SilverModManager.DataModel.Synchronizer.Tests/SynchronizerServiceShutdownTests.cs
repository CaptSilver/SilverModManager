using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Hashes;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A status evaluation reads the database through DuckDB on the thread pool. The service has to stop starting them
/// and wait for the running ones when it is disposed, which the container does before it closes the connection.
/// </summary>
public class SynchronizerServiceShutdownTests(ITestOutputHelper helper) : AStubbedGameTest<SynchronizerServiceShutdownTests>(helper)
{
    private readonly HashesGate _gate = new();

    private sealed class HashesGate
    {
        private readonly ManualResetEventSlim _open = new(true);
        public readonly ManualResetEventSlim Entered = new();
        public int Calls;

        public void Close() { _open.Reset(); Entered.Reset(); }
        public void Open() => _open.Set();

        public async Task PassAsync()
        {
            Interlocked.Increment(ref Calls);
            Entered.Set();
            await Task.Run(() => _open.Wait(TimeSpan.FromSeconds(30)));
        }
    }

    private sealed class GatedHashes(IServiceProvider provider, IFileStore fileStore, TemporaryFileManager temporaryFiles, HashesGate gate)
        : StubbedFileHasherService(provider, fileStore, temporaryFiles)
    {
        public override async ValueTask<IDb> GetFileHashesDb()
        {
            await gate.PassAsync();
            return await base.GetFileHashesDb();
        }
    }

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        var gate = _gate;
        base.AddServices(services);
        var real = services.Last(d => d.ServiceType == typeof(IFileHashesService));
        services.Remove(real);
        return services.AddSingleton<IFileHashesService>(sp => ActivatorUtilities.CreateInstance<GatedHashes>(sp, gate));
    }

    [Fact]
    public async Task DisposeWaitsForAStatusEvaluationThatIsStillRunning()
    {
        var loadout = await CreateLoadout();
        _gate.Close();
        var status = await SynchronizerService.StatusForLoadout(loadout.LoadoutId);
        using var watch = status.Subscribe(_ => { });
        _gate.Entered.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("the evaluation starts on subscribe");

        var dispose = Task.Run(((IDisposable)SynchronizerService).Dispose);
        (await Task.WhenAny(dispose, Task.Delay(500))).Should().NotBeSameAs(dispose,
            "Dispose returned while an evaluation was still reading the database");

        _gate.Open();
        await dispose.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task NoEvaluationStartsOnceTheServiceIsDisposed()
    {
        var loadout = await CreateLoadout();
        ((IDisposable)SynchronizerService).Dispose();
        var before = Volatile.Read(ref _gate.Calls);

        var status = await SynchronizerService.StatusForLoadout(loadout.LoadoutId);
        using var watch = status.Subscribe(_ => { });
        await Task.Delay(500);

        Volatile.Read(ref _gate.Calls).Should().Be(before, "a disposed service leaves the database alone");
    }
}
