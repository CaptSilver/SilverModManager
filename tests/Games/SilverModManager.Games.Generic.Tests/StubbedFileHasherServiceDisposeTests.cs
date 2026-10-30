using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Backend;
using SilverModManager.Games.TestFramework;
using SilverModManager.StandardGameLocators.TestHelpers;
using RocksDbBackend = SilverModManager.MnemonicDB.Storage.RocksDbBackend.Backend;

namespace SilverModManager.Games.Generic.Tests;

/// <summary>
/// The stubbed hashes service owns two databases of its own. A test host that leaves them open reaches native exit()
/// with RocksDB's background threads still running.
/// </summary>
public class StubbedFileHasherServiceDisposeTests
{
    [Fact]
    public async Task DisposingTheContainerClosesTheDatabasesTheStubbedHashesServiceOpened()
    {
        var provider = new ServiceCollection()
            .AddDefaultServicesForTesting()
            .AddLogging()
            .AddSingleton<Xunit.DependencyInjection.ITestOutputHelperAccessor, Xunit.DependencyInjection.TestOutputHelperAccessor>()
            .AddGames()
            .AddGameServices()
            .BuildServiceProvider();

        var service = (StubbedFileHasherService)provider.GetRequiredService<IFileHashesService>();
        _ = service.Local;
        _ = service.Current;

        var backends = service.Backends.Cast<RocksDbBackend>().ToArray();
        backends.Should().HaveCount(2, "the local store and the hashes store");
        foreach (var backend in backends)
            RocksDbBackend.OpenBackends().Should().Contain(backend);

        await provider.DisposeAsync();

        foreach (var backend in backends)
        {
            var begin = () => backend.BeginUse();
            begin.Should().Throw<ObjectDisposedException>("the service that opened the database closes it");
        }
    }
}
