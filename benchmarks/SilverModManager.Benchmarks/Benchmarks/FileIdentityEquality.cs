using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Benchmarks.Interfaces;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Benchmarks.Benchmarks;

/// <summary>
/// What comparing two xxHash3 identities costs next to comparing the bare <see cref="Hash"/>. Informational: the
/// sync tree compares identities once per node part, so only a large gap would matter.
/// </summary>
[BenchmarkInfo("File identity equality", "Compares xxHash3 values as a bare Hash, as FileIdentity.Equals and through MatchSameKind.")]
[MemoryDiagnoser]
[Config(typeof(InProcessShortRunConfig))]
public class FileIdentityEquality : IBenchmark
{
    private const int Count = 100_000;

    private Hash[] _hashes = [];
    private Hash[] _otherHashes = [];
    private FileIdentity[] _identities = [];
    private FileIdentity[] _otherIdentities = [];

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(7);
        _hashes = new Hash[Count];
        _otherHashes = new Hash[Count];
        _identities = new FileIdentity[Count];
        _otherIdentities = new FileIdentity[Count];
        for (var i = 0; i < Count; i++)
        {
            _hashes[i] = Hash.From((ulong)random.NextInt64());
            // Half the pairs are equal, so neither branch of a compare is the only one measured.
            _otherHashes[i] = i % 2 == 0 ? _hashes[i] : Hash.From((ulong)random.NextInt64());
            _identities[i] = FileIdentity.XxHash3(_hashes[i]);
            _otherIdentities[i] = FileIdentity.XxHash3(_otherHashes[i]);
        }
    }

    [Benchmark(Baseline = true)]
    public int BareHash()
    {
        var same = 0;
        for (var i = 0; i < Count; i++)
        {
            if (_hashes[i] == _otherHashes[i]) same++;
        }

        return same;
    }

    [Benchmark]
    public int IdentityEquals()
    {
        var same = 0;
        for (var i = 0; i < Count; i++)
        {
            if (_identities[i].Equals(_otherIdentities[i])) same++;
        }

        return same;
    }

    [Benchmark]
    public int IdentityMatchSameKind()
    {
        var same = 0;
        for (var i = 0; i < Count; i++)
        {
            if (_identities[i].MatchSameKind(_otherIdentities[i]) == IdentityMatch.Same) same++;
        }

        return same;
    }
}
