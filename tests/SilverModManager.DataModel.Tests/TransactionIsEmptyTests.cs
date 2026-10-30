using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.MnemonicDB;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Storage;
using SilverModManager.MnemonicDB.Storage.Abstractions;
using SilverModManager.Sdk.Games;
using RocksDbBackend = SilverModManager.MnemonicDB.Storage.RocksDbBackend.Backend;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// A commit takes a transaction id and wakes every observer of the database even when it writes nothing, so callers
/// that may have nothing to write need to ask first.
/// </summary>
public class TransactionIsEmptyTests
{
    private static ServiceProvider CreateProvider() => new ServiceCollection()
        .AddLogging()
        .AddMnemonicDB()
        .AddDatomStoreSettings(new DatomStoreSettings())
        .AddSingleton<IStoreBackend, RocksDbBackend>()
        .AddGameInstallMetadataModel()
        .BuildServiceProvider();

    [Fact]
    public async Task ANewTransactionIsEmpty()
    {
        await using var provider = CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();

        using var tx = connection.BeginTransaction();

        tx.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task AnAddedDatomMakesATransactionNotEmpty()
    {
        await using var provider = CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();

        using var tx = connection.BeginTransaction();
        tx.Add(tx.TempId(), GameInstallMetadata.Name, "a game");

        tx.IsEmpty.Should().BeFalse();
    }
}
