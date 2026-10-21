using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.MnemonicDB;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Storage;
using NexusMods.MnemonicDB.Storage.RocksDbBackend;
using NexusMods.Paths;

namespace NexusMods.Games.FileHashes;

/// <summary>
/// The writable store of rows this app builds from the game's own install data. Unlike the downloaded
/// databases it is never replaced, and the query engine knows it as <see cref="ConnectionName"/>.
/// </summary>
internal sealed class LocalHashesStore : IDisposable
{
    /// <summary>
    /// The name SQL addresses this store by: <c>MDB_HashRelation(DbName=>'local_hashes')</c>.
    /// </summary>
    public const string ConnectionName = "local_hashes";

    private readonly Backend _backend;
    private readonly DatomStore _store;

    public LocalHashesStore(IServiceProvider provider, IQueryEngine queryEngine, FileHashesServiceSettings settings, IFileSystem fileSystem)
    {
        // The downloaded databases open read-only, but this one gets written to, so it needs the default backend.
        _backend = new Backend();

        var storeSettings = DatomStoreSettings.InMemory;
        if (!settings.UseInMemoryLocalBaseline)
        {
            var path = settings.LocalBaselineLocation.ToPath(fileSystem);
            path.CreateDirectory();
            storeSettings = new DatomStoreSettings { Path = path };
        }

        try
        {
            _store = new DatomStore(provider.GetRequiredService<ILogger<DatomStore>>(), storeSettings, _backend);
            Connection = new Connection(provider.GetRequiredService<ILogger<Connection>>(), _store, provider, [], readOnlyMode: false, prefix: ConnectionName, queryEngine: queryEngine);
        }
        catch
        {
            _store?.Dispose();
            _backend.Dispose();
            throw;
        }
    }

    public IConnection Connection { get; }

    public void Dispose()
    {
        // Disposing the connection is what unregisters its name from the query engine.
        ((Connection)Connection).Dispose();
        _store.Dispose();
        _backend.Dispose();
    }
}
