using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Hashing.xxHash3;
using NSubstitute;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Benchmarks.Interfaces;
using SilverModManager.Games.FileHashes;
using SilverModManager.MnemonicDB;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Storage;
using RocksBackend = SilverModManager.MnemonicDB.Storage.RocksDbBackend.Backend;
using SilverModManager.Sdk.Hashes;
using Connection = SilverModManager.MnemonicDB.Connection;
using NexusMods.Paths;

namespace SilverModManager.Benchmarks.Benchmarks.FileHashes;

/// <summary>
/// Opens a scoped resolver over a downloaded-like database of recorded hash rows and converts every expected SHA-1,
/// half of which a row records. Forced to walk the attribute, forced to seek, and left to choose.
/// </summary>
[BenchmarkInfo("Recorded digest scope", "Scoped recorded-digest lookups by database size and number of expected identities, walking the attribute, seeking per identity, or letting the resolver choose.")]
[MemoryDiagnoser]
[Config(typeof(InProcessShortRunConfig))]
public class RecordedDigestScope : IBenchmark
{
    public enum Path
    {
        /// <summary>The resolver picks, as production does.</summary>
        Chosen,
        /// <summary>Always walks the attribute of every database.</summary>
        Scan,
        /// <summary>Always seeks for each identity.</summary>
        Seek,
    }

    private ServiceProvider _provider = null!;
    private readonly List<IDisposable> _owned = [];
    private IFileHashesService _hashes = null!;
    private FileIdentity[] _expected = [];
    private Size[] _sizes = [];
    private RecordedDigestResolver _resolver = null!;

    /// <summary>Rows in the downloaded-like database. The local store holds a tenth of it.</summary>
    [Params(2_000, 500_000)]
    public int Rows { get; set; }

    [Params(40, 20_000)]
    public int Expected { get; set; }

    [Params(Path.Chosen, Path.Scan, Path.Seek)]
    public Path Strategy { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _provider = new ServiceCollection().AddLogging().AddMnemonicDB().AddHashRelationModel().BuildServiceProvider();
        var local = Open("local", Rows / 10, offset: 0);
        var downloaded = Open("downloaded", Rows, offset: Rows);
        _hashes = Substitute.For<IFileHashesService>();
        _hashes.Databases.Returns(new[] { local.Db, downloaded.Db });

        // Half the expected identities are rows of the downloaded database, the rest are in no database.
        _expected = new FileIdentity[Expected];
        _sizes = new Size[Expected];
        for (var i = 0; i < Expected; i++)
        {
            var row = i % 2 == 0 ? Row(Rows + (i / 2) % Rows) : Row(-1 - i);
            _expected[i] = FileIdentity.Sha1(row.Sha1);
            _sizes[i] = row.Size;
        }

        _resolver = new RecordedDigestResolver(_hashes, Strategy switch
        {
            Path.Scan => int.MaxValue,
            Path.Seek => 0,
            _ => RecordedDigestResolver.ScanRowsPerLookup,
        });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _provider.Dispose();
    }

    [Benchmark]
    public int ScopeAndConvert()
    {
        var scoped = _resolver.Scoped(_expected);
        var found = 0;
        for (var i = 0; i < _expected.Length; i++)
        {
            if (scoped.TryConvert(_expected[i], _sizes[i], HashKind.XxHash3, out _)) found++;
        }

        return found;
    }

    private IConnection Open(string name, int rows, int offset)
    {
        var backend = new RocksBackend();
        var store = new DatomStore(_provider.GetRequiredService<ILogger<DatomStore>>(), DatomStoreSettings.InMemory, backend);
        var connection = new Connection(_provider.GetRequiredService<ILogger<Connection>>(), store, _provider, [], prefix: name);
        _owned.Add(connection);
        _owned.Add(store);
        _owned.Add(backend);

        using var tx = connection.BeginTransaction();
        for (var i = 0; i < rows; i++)
        {
            var row = Row(offset + i);
            _ = new HashRelation.New(tx)
            {
                MinimalHash = row.XxHash3, XxHash3 = row.XxHash3, XxHash64 = row.XxHash64, Sha1 = row.Sha1,
                Md5 = row.Md5, Crc32 = row.Crc32, Size = row.Size,
            };
        }

        tx.Commit().GetAwaiter().GetResult();
        return connection;
    }

    private readonly record struct RowHashes(Hash MinimalHash, Hash XxHash3, Hash XxHash64, Sha1Value Sha1, Md5Value Md5, Crc32Value Crc32, Size Size);

    private static RowHashes Row(long seed)
    {
        var a = Mix((ulong)seed);
        var b = Mix(a);
        var c = Mix(b);
        Span<byte> sha1 = stackalloc byte[20];
        BitConverter.TryWriteBytes(sha1, a);
        BitConverter.TryWriteBytes(sha1[8..], b);
        BitConverter.TryWriteBytes(sha1[16..], (uint)c);
        return new RowHashes(Hash.From(a), Hash.From(b), Hash.From(c), Sha1Value.From(sha1), Md5Value.From(new UInt128(a, c)), Crc32Value.From((uint)b), Size.From(1 + a % 1_000_000));
    }

    private static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
}
