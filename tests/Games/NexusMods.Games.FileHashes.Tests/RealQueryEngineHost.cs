using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games.FileHashes.Models;
using NexusMods.Hashing.xxHash3;
using NexusMods.HyperDuck;
using NexusMods.MnemonicDB;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Storage;
using NexusMods.MnemonicDB.Storage.RocksDbBackend;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Loadouts;
using Connection = NexusMods.MnemonicDB.Connection;

namespace NexusMods.Games.FileHashes.Tests;

/// <summary>
/// A service provider with the real query engine and the hash models, and nothing else: no settings
/// manager and no hosted services. It is not built from the test framework's helper, which registers a
/// stubbed hashes service under the "hashes" name and would collide with the connections these tests open.
/// </summary>
internal sealed class RealQueryEngineHost : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly List<IDisposable> _owned = [];

    /// <param name="extraSql">Statements run next to the file_hashes macros, for tests that exercise the SQL engine itself.</param>
    public RealQueryEngineHost(params string[] extraSql)
    {
        var services = new ServiceCollection();
        for (var i = 0; i < extraSql.Length; i++) services.AddSingleton(new AmbientSqlFragment($"test-{i}", extraSql[i]));
        _provider = services
            .AddLogging()
            .AddMnemonicDB()
            .AddHashRelationModel()
            .AddPathHashRelationModel()
            .AddVersionDefinitionModel()
            .AddGogBuildModel()
            .AddGogDepotModel()
            .AddGogManifestModel()
            .AddSteamManifestModel()
            .AddLocalBaselineModel()
            .AddNonVanillaFileModel()
            .AddEpicGameStoreBuildModel()
            .AddLoadoutModel()
            .AddGameInstallMetadataModel()
            .AddFileHashesQueriesSql()
            .BuildServiceProvider();
    }

    public IServiceProvider Provider => _provider;

    public IQueryEngine QueryEngine => _provider.GetRequiredService<IQueryEngine>();

    /// <summary>
    /// Opens a writable in-memory connection registered under <paramref name="name"/>. The caller
    /// owns nothing: the host disposes it, unless <see cref="DisposeConnection"/> got there first.
    /// </summary>
    public IConnection OpenInMemory(string name)
    {
        var backend = new Backend();
        var store = new DatomStore(_provider.GetRequiredService<ILogger<DatomStore>>(), DatomStoreSettings.InMemory, backend);
        var connection = new Connection(_provider.GetRequiredService<ILogger<Connection>>(), store, _provider, [], prefix: name, queryEngine: QueryEngine);
        _owned.Add(connection);
        _owned.Add(store);
        _owned.Add(backend);
        return connection;
    }

    public static async Task<Hash> AddHashRow(IConnection connection, string content)
    {
        var hashes = await MultiHasher.HashStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)));
        using var tx = connection.BeginTransaction();
        _ = new HashRelation.New(tx)
        {
            MinimalHash = hashes.MinimalHash,
            XxHash3 = hashes.XxHash3,
            Sha1 = hashes.Sha1,
            XxHash64 = hashes.XxHash64,
            Md5 = hashes.Md5,
            Size = hashes.Size,
            Crc32 = hashes.Crc32,
        };
        await tx.Commit();
        return hashes.XxHash3;
    }

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        // The query engine is async-disposable only.
        _provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
