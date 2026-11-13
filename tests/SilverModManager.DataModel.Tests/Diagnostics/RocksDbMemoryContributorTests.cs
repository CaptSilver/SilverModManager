using FluentAssertions;
using NSubstitute;
using SilverModManager.DataModel.Diagnostics;
using SilverModManager.MnemonicDB.Storage.Abstractions;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Storage.RocksDbBackend;
using RocksDbBackend = SilverModManager.MnemonicDB.Storage.RocksDbBackend.Backend;

namespace SilverModManager.DataModel.Tests.Diagnostics;

public class RocksDbMemoryContributorTests
{
    [Fact]
    public void MappedLibraryFindsTheRocksDbSonameInAMapsText()
    {
        const string plain = "7f00-7f01 r-xp 00000000 08:01 12 /usr/lib/x86_64-linux-gnu/librocksdb.so\n7f02-7f03 rw-p 0 00:00 0\n";
        const string jemalloc = "7f00-7f01 r-xp 00000000 08:01 12 /app/runtimes/linux-x64/native/librocksdb-jemalloc.so\n";

        RocksDbMemoryContributor.MappedLibrary(plain).Should().Be("librocksdb.so");
        RocksDbMemoryContributor.MappedLibrary(jemalloc).Should().Be("librocksdb-jemalloc.so");
        RocksDbMemoryContributor.MappedLibrary("7f00-7f01 r-xp 0 08:01 12 /usr/lib/libc.so.6\n").Should().BeNull();
    }

    [Fact]
    public void ParsePropertyReadsAnIntegerAndGivesZeroForNull()
    {
        RocksDbMemoryReport.ParseProperty("12345").Should().Be(12345);
        RocksDbMemoryReport.ParseProperty(null).Should().Be(0);
        RocksDbMemoryReport.ParseProperty("not a number").Should().Be(0);
    }

    [Fact]
    public void AClosedBackendContributesNothing()
    {
        var backend = new RocksDbBackend();
        backend.Init(DatomStoreSettings.InMemory);
        backend.Dispose();

        new RocksDbMemoryContributor(backend).Sample().Should().BeEmpty();
    }

    [Fact]
    public void ABackendThatWasNeverOpenedContributesNothing()
    {
        new RocksDbMemoryContributor(new RocksDbBackend()).Sample().Should().BeEmpty();
    }

    [Fact]
    public void AnotherBackendContributesNothing()
    {
        new RocksDbMemoryContributor(Substitute.For<IStoreBackend>()).Sample().Should().BeEmpty();
    }

    [Fact]
    public void AnOpenBackendReportsItsCountersUnderEveryKey()
    {
        var backend = new RocksDbBackend();
        backend.Init(DatomStoreSettings.InMemory);
        try
        {
            var report = backend.Sample();

            report.Should().NotBeNull();
            report!.TableReaders.Should().BeGreaterThanOrEqualTo(0);
            report.ApproximateTotal.Should().BeGreaterThan(0);
            report.BlockCacheCapacity.Should().BeGreaterThan(0);

            var samples = new RocksDbMemoryContributor(backend).Sample();
            samples.Select(s => s.Key).Should().Contain(["rocksdb-memtables", "rocksdb-readers", "rocksdb-cache", "rocksdb-cache-pinned", "rocksdb-cache-capacity"]);
        }
        finally
        {
            backend.Dispose();
        }
    }
}
