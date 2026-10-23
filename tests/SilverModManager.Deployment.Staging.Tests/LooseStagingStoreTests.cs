using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Native;
using SilverModManager.Sdk.Settings;
using NSubstitute;

namespace SilverModManager.Deployment.Staging.Tests;

public class LooseStagingStoreTests
{
    private static readonly GameInstallMetadataId InstallA = GameInstallMetadataId.From(EntityId.From(0xA1));
    private static readonly GameInstallMetadataId InstallB = GameInstallMetadataId.From(EntityId.From(0xB2));

    private readonly LooseStagingStore _store;
    private readonly FakeFileStoreContents _contents;
    private readonly IFileStore _fileStore;
    private readonly IServiceProvider _serviceProvider;
    private ISettingsManager Settings { get; }

    // Each test gets its own store over a fresh directory, so tests stay independent of one another.
    public LooseStagingStoreTests(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _contents = new FakeFileStoreContents();
        _fileStore = FakeFileStoreContents.CreateFileStore(_contents);
        var fileSystem = serviceProvider.GetRequiredService<IFileSystem>();
        var location = fileSystem.GetKnownPath(KnownPath.TempDirectory).Combine($"loose-staging-{Guid.NewGuid():N}");
        Settings = Substitute.For<ISettingsManager>();
        Settings.Get<LooseStagingStoreSettings>().Returns(new LooseStagingStoreSettings
        {
            Location = new ConfigurablePath(null, location.ToString()),
        });
        _store = new LooseStagingStore(Settings, _fileStore, fileSystem, new StatFilesystemLocator(), Microsoft.Extensions.Logging.Abstractions.NullLogger<LooseStagingStore>.Instance);
    }

    // The first byte of the hash picks the two-character directory the file lands in.
    private Hash Add(byte firstByte, ulong rest, string content = "x")
    {
        var h = Hash.From((ulong)firstByte << 56 | rest);
        _contents.Files[h] = System.Text.Encoding.UTF8.GetBytes(content);
        return h;
    }

    [Fact]
    public void PathFor_UsesInstallationHashPrefixAndSixteenHexDigits()
    {
        var path = _store.PathFor(InstallA, Hash.From(0xab00_0000_0000_0cdeUL));

        path.Should().Be(_store.Root.Combine("a1").Combine("ab").Combine("ab00000000000cde"));
    }

    [Fact]
    public async Task Ensure_TwiceForOneHash_ExtractsOnce()
    {
        var hash = Add(0xab, 0x01);

        var first = await _store.Ensure(InstallA, hash, TestContext.Current.CancellationToken);
        var second = await _store.Ensure(InstallA, hash, TestContext.Current.CancellationToken);

        second.Should().Be(first);
        first.FileExists.Should().BeTrue();
        _contents.ExtractedTo.Should().HaveCount(1);
        _store.Enumerate(InstallA).Should().HaveCount(1);
    }

    [Fact]
    public async Task Ensure_SameHashForTwoInstallations_StagesTwoFiles()
    {
        var hash = Add(0xab, 0x02);

        var a = await _store.Ensure(InstallA, hash, TestContext.Current.CancellationToken);
        var b = await _store.Ensure(InstallB, hash, TestContext.Current.CancellationToken);

        a.Should().NotBe(b);
        a.FileExists.Should().BeTrue();
        b.FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task Ensure_WithStaleTempFile_SucceedsAndLeavesNoTempFile()
    {
        var hash = Add(0xab, 0x03, "fresh");
        var final = _store.PathFor(InstallA, hash);
        var stale = final.Parent.Combine($".{final.FileName}.tmp");
        stale.Parent.CreateDirectory();
        await stale.WriteAllTextAsync("half a file", TestContext.Current.CancellationToken);

        var path = await _store.Ensure(InstallA, hash, TestContext.Current.CancellationToken);

        (await path.ReadAllTextAsync(TestContext.Current.CancellationToken)).Should().Be("fresh");
        stale.FileExists.Should().BeFalse();
        path.Parent.EnumerateFiles().Should().ContainSingle().Which.Should().Be(path);
    }

    [Fact]
    public async Task Contains_ReflectsWhetherTheFileIsStaged()
    {
        var hash = Add(0xab, 0x04);

        _store.Contains(InstallA, hash).Should().BeFalse();
        await _store.Ensure(InstallA, hash, TestContext.Current.CancellationToken);
        _store.Contains(InstallA, hash).Should().BeTrue();
        _store.Contains(InstallB, hash).Should().BeFalse();
    }

    [Fact]
    public async Task EnumerateAndInstallations_ListWhatIsStaged()
    {
        var h1 = Add(0xab, 0x05);
        var h2 = Add(0xcd, 0x06);
        await _store.Ensure(InstallA, h1, TestContext.Current.CancellationToken);
        await _store.Ensure(InstallA, h2, TestContext.Current.CancellationToken);
        await _store.Ensure(InstallB, h1, TestContext.Current.CancellationToken);

        _store.Enumerate(InstallA).Select(x => x.Hash).Should().BeEquivalentTo([h1, h2]);
        _store.Enumerate(InstallA).Should().OnlyContain(x => x.Path == _store.PathFor(InstallA, x.Hash));
        _store.Installations().Should().BeEquivalentTo([InstallA, InstallB]);
    }

    [Fact]
    public async Task Sweep_DeletesFilesNotLiveAndWholeAbsentInstallations()
    {
        var h1 = Add(0xab, 0x07);
        var h2 = Add(0xcd, 0x08);
        var h3 = Add(0xef, 0x09);
        await _store.Ensure(InstallA, h1, TestContext.Current.CancellationToken);
        await _store.Ensure(InstallA, h2, TestContext.Current.CancellationToken);
        await _store.Ensure(InstallB, h3, TestContext.Current.CancellationToken);

        var deleted = _store.Sweep(() => new Dictionary<GameInstallMetadataId, IReadOnlySet<Hash>>
        {
            [InstallA] = new HashSet<Hash> { h1 },
        });

        deleted.Should().Be(2);
        _store.Contains(InstallA, h1).Should().BeTrue();
        _store.Contains(InstallA, h2).Should().BeFalse();
        _store.Installations().Should().BeEquivalentTo([InstallA]);
        // The hh directory that held the only dead file goes too.
        _store.PathFor(InstallA, h2).Parent.DirectoryExists().Should().BeFalse();
        _store.Root.Combine("b2").DirectoryExists().Should().BeFalse();
    }

    [Fact]
    public void MissingRoot_SweepEnumerateAndInstallations_AreEmptyAndCreateNothing()
    {
        _store.Root.DirectoryExists().Should().BeFalse();

        _store.Sweep(() => new Dictionary<GameInstallMetadataId, IReadOnlySet<Hash>>()).Should().Be(0);
        _store.Enumerate(InstallA).Should().BeEmpty();
        _store.Installations().Should().BeEmpty();
        _store.Contains(InstallA, Hash.From(1)).Should().BeFalse();

        _store.Root.DirectoryExists().Should().BeFalse();
    }

    private static readonly TimeSpan NotYet = TimeSpan.FromMilliseconds(200);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LibcLink(string existing, string newPath);

    [Fact]
    public async Task Sweep_WaitsForAnOpenHold()
    {
        var hash = Add(0xab, 0x10);
        var path = await _store.Ensure(InstallA, hash, TestContext.Current.CancellationToken);

        var hold = _store.Hold();
        var sweep = Task.Run(() => _store.Sweep(() => new Dictionary<GameInstallMetadataId, IReadOnlySet<Hash>>()), TestContext.Current.CancellationToken);

        (await Task.WhenAny(sweep, Task.Delay(NotYet, TestContext.Current.CancellationToken))).Should().NotBe(sweep);
        path.FileExists.Should().BeTrue();

        hold.Dispose();
        (await sweep.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Should().Be(1);
        path.FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task Hold_WaitsForARunningSweep()
    {
        var hash = Add(0xab, 0x11);
        await _store.Ensure(InstallA, hash, TestContext.Current.CancellationToken);

        using var inLive = new ManualResetEventSlim();
        using var finishLive = new ManualResetEventSlim();
        var sweep = Task.Run(() => _store.Sweep(() =>
        {
            inLive.Set();
            finishLive.Wait();
            return new Dictionary<GameInstallMetadataId, IReadOnlySet<Hash>> { [InstallA] = new HashSet<Hash> { hash } };
        }), TestContext.Current.CancellationToken);
        inLive.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).Should().BeTrue();

        var hold = Task.Run(() => _store.Hold(), TestContext.Current.CancellationToken);
        (await Task.WhenAny(hold, Task.Delay(NotYet, TestContext.Current.CancellationToken))).Should().NotBe(hold);

        finishLive.Set();
        await sweep.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        (await hold.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Dispose();
    }

    [Fact]
    public async Task Hold_CanBeTakenAgainWhileHeld()
    {
        using var outer = _store.Hold();
        using var inner = _store.Hold();

        var again = Task.Run(() => _store.Hold(), TestContext.Current.CancellationToken);
        (await again.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Dispose();
    }

    [Fact]
    public async Task Hold_DisposedTwice_ReleasesOnlyItself()
    {
        var hash = Add(0xab, 0x17);
        var path = await _store.Ensure(InstallA, hash, TestContext.Current.CancellationToken);

        var other = _store.Hold();
        var hold = _store.Hold();
        hold.Dispose();
        hold.Dispose();

        var sweep = Task.Run(() => _store.Sweep(() => new Dictionary<GameInstallMetadataId, IReadOnlySet<Hash>>()), TestContext.Current.CancellationToken);
        (await Task.WhenAny(sweep, Task.Delay(NotYet, TestContext.Current.CancellationToken))).Should().NotBe(sweep, "the other hold is still open");
        path.FileExists.Should().BeTrue();

        other.Dispose();
        await sweep.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Sweep_ReadsLivenessOnlyAfterTheHoldsAreReleased()
    {
        var hash = Add(0xab, 0x12);
        await _store.Ensure(InstallA, hash, TestContext.Current.CancellationToken);

        var liveRan = false;
        var hold = _store.Hold();
        var sweep = Task.Run(() => _store.Sweep(() =>
        {
            liveRan = true;
            return new Dictionary<GameInstallMetadataId, IReadOnlySet<Hash>> { [InstallA] = new HashSet<Hash> { hash } };
        }), TestContext.Current.CancellationToken);

        await Task.Delay(NotYet, TestContext.Current.CancellationToken);
        liveRan.Should().BeFalse();

        hold.Dispose();
        await sweep.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        liveRan.Should().BeTrue();
        _store.Contains(InstallA, hash).Should().BeTrue();
    }

    [Fact]
    public async Task Release_UnlinksOnlyTheStorePath()
    {
        var hash = Add(0xab, 0x13, "payload");
        var path = await _store.Ensure(InstallA, hash, TestContext.Current.CancellationToken);
        var sibling = path.Parent.Parent.Combine("linked");
        LibcLink(path.ToString(), sibling.ToString()).Should().Be(0);
        LibcStat.TryStat(sibling.ToString(), out var before).Should().Be(0);
        before.NLink.Should().Be(2);

        _store.Release(InstallA, hash).Should().BeTrue();

        path.FileExists.Should().BeFalse();
        _store.Contains(InstallA, hash).Should().BeFalse();
        (await sibling.ReadAllTextAsync(TestContext.Current.CancellationToken)).Should().Be("payload");
        LibcStat.TryStat(sibling.ToString(), out var after).Should().Be(0);
        after.NLink.Should().Be(1);

        await _store.Ensure(InstallA, hash, TestContext.Current.CancellationToken);
        _contents.ExtractedTo.Should().HaveCount(2);
        _store.Release(InstallB, hash).Should().BeFalse();
    }

    [Fact]
    public void DirectoryFor_CreatesTheInstallationDirectory()
    {
        var directory = _store.DirectoryFor(InstallA);

        directory.Should().Be(_store.Root.Combine("a1"));
        directory.DirectoryExists().Should().BeTrue();
    }

    [Fact]
    public async Task Sweep_RemovesStaleTempAndProbeFiles()
    {
        var live = Add(0xab, 0x14);
        var path = await _store.Ensure(InstallA, live, TestContext.Current.CancellationToken);
        var staleTemp = path.Parent.Combine(".ab00000000000099.tmp");
        var stalePrefix = _store.Root.Combine("a1").Combine("cd");
        var staleProbe = stalePrefix.Combine($"{ProtonMessages.ProbeFileName}-0123");
        var rootProbe = _store.DirectoryFor(InstallB).Combine($"{ProtonMessages.ProbeFileName}-4567");
        var other = path.Parent.Combine("notes.txt");
        stalePrefix.CreateDirectory();
        foreach (var file in new[] { staleTemp, staleProbe, rootProbe, other })
        {
            await file.WriteAllTextAsync("x", TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(file.ToString(), DateTime.UtcNow.AddHours(-1));
        }

        var deleted = _store.Sweep(() => new Dictionary<GameInstallMetadataId, IReadOnlySet<Hash>>
        {
            [InstallA] = new HashSet<Hash> { live },
            [InstallB] = new HashSet<Hash>(),
        });

        deleted.Should().Be(0, "only staged files are counted");
        staleTemp.FileExists.Should().BeFalse();
        staleProbe.FileExists.Should().BeFalse();
        stalePrefix.DirectoryExists().Should().BeFalse("the probe was all that kept it");
        rootProbe.FileExists.Should().BeFalse();
        _store.Root.Combine("b2").DirectoryExists().Should().BeFalse("the probe was all that kept it");
        other.FileExists.Should().BeTrue("it is not a temp or probe file");
        path.FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task Ensure_DropsItsGateAfterwards()
    {
        var hash = Add(0xab, 0x15);

        await _store.Ensure(InstallA, hash, TestContext.Current.CancellationToken);

        _store.GateCount.Should().Be(0);
    }

    [Fact]
    public async Task Ensure_KeepsTheGateWhileAnotherCallerWaits()
    {
        var hash = Add(0xab, 0x16);
        var calls = 0;
        var firstFails = new TaskCompletionSource();
        var secondStarted = new TaskCompletionSource();
        var secondMayFinish = new TaskCompletionSource();
        var fileStore = Substitute.For<IFileStore>();
        fileStore.ExtractFiles(Arg.Any<IEnumerable<(Hash, AbsolutePath)>>(), Arg.Any<CancellationToken>(), Arg.Any<Action<(int, int)>?>())
            .Returns(async call =>
            {
                var n = Interlocked.Increment(ref calls);
                if (n == 1)
                {
                    await firstFails.Task;
                    throw new IOException("extraction failed");
                }
                var (_, dest) = call.Arg<IEnumerable<(Hash, AbsolutePath)>>().Single();
                secondStarted.TrySetResult();
                await secondMayFinish.Task;
                dest.Parent.CreateDirectory();
                await dest.WriteAllTextAsync("x", CancellationToken.None);
            });
        var store = new LooseStagingStore(Settings, fileStore, _serviceProvider.GetRequiredService<IFileSystem>(), new StatFilesystemLocator(), Microsoft.Extensions.Logging.Abstractions.NullLogger<LooseStagingStore>.Instance);

        var first = store.Ensure(InstallA, hash, TestContext.Current.CancellationToken).AsTask();
        var second = store.Ensure(InstallA, hash, TestContext.Current.CancellationToken).AsTask();
        firstFails.SetResult();
        await Assert.ThrowsAsync<IOException>(() => first);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // A newcomer must queue behind the second caller instead of extracting beside it.
        var third = store.Ensure(InstallA, hash, TestContext.Current.CancellationToken).AsTask();
        await Task.Delay(NotYet, TestContext.Current.CancellationToken);
        calls.Should().Be(2);

        secondMayFinish.SetResult();
        await Task.WhenAll(second, third).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        calls.Should().Be(2);
        store.GateCount.Should().Be(0);
    }

    [Fact]
    public void Settings_HaveForkNamedIdAndDefaultUnderTheDataDirectory()
    {
        LooseStagingStoreSettings.SettingsId.Should().Be("SilverModManager.Deployment.LooseStagingStoreSettings");

        var defaults = LooseStagingStoreSettings.CreateDefault(null!);

        defaults.Location.Should().Be(new ConfigurablePath(KnownPath.XDG_DATA_HOME, "SilverModManager/DataModel/LooseStaging"));
    }

    [Fact]
    public void AddLooseStagingStore_ResolvesTheStoreFromTheSettingsLocation()
    {
        var store = _serviceProvider.GetRequiredService<ILooseStagingStore>();

        store.Should().BeOfType<LooseStagingStore>();
        store.Root.DirectoryExists().Should().BeFalse();
    }
}
