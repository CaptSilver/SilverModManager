using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.App;
using NexusMods.MnemonicDB;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Storage;
using NexusMods.MnemonicDB.Storage.Abstractions;
using RocksDbBackend = NexusMods.MnemonicDB.Storage.RocksDbBackend.Backend;
using NexusMods.Paths;

namespace NexusMods.CLI.Tests;

/// <summary>
/// Opens real RocksDB stores in a temp directory, because the lock file is what the next process trips over.
/// </summary>
public sealed class DatabaseCloseTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("database-close-tests").FullName;
    private readonly List<ServiceProvider> _providers = [];

    private AbsolutePath DatabasePath => FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_directory, "MnemonicDB.rocksdb"));

    private ServiceProvider OpenStore()
    {
        DatabasePath.CreateDirectory();
        var provider = new ServiceCollection()
            .AddLogging()
            .AddDatomStoreSettings(new DatomStoreSettings { Path = DatabasePath })
            .AddSingleton<IStoreBackend>(_ => new RocksDbBackend())
            .AddSingleton<IDatomStore, DatomStore>()
            .AddSingleton<IConnection, Connection>()
            .BuildServiceProvider();
        _providers.Add(provider);

        provider.GetRequiredService<IConnection>();
        return provider;
    }

    [Fact]
    public void ClosingTheDatabaseLetsAnotherStoreOpenTheSamePath()
    {
        var first = OpenStore();

        Program.CloseDatabase(first);

        var reopen = () => OpenStore();
        reopen.Should().NotThrow("the slot is released right after this, and the next main opens the database at once");
    }

    [Fact]
    public void OpeningALockedDatabaseIsRecognisedAsLocked()
    {
        OpenStore();

        // RocksDB tracks its locks per process, so this is the "lock hold by current process" text. Another
        // process gets "While lock file: ...: Resource temporarily unavailable" instead, which a test cannot
        // produce without starting a second process.
        var reopen = () => OpenStore();

        reopen.Should().Throw<Exception>().Which.Should().Match<Exception>(e => ProcessStartup.IsDatabaseLocked(e));
    }

    public void Dispose()
    {
        foreach (var provider in Enumerable.Reverse(_providers)) provider.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
