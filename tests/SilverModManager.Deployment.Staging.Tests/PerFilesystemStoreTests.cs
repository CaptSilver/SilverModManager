using FluentAssertions;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts.Deployment;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Native;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Settings;
using NSubstitute;

namespace SilverModManager.Deployment.Staging.Tests;

/// <summary>
/// Stores on other filesystems. The filesystem layout is faked: a "device" is a temp directory that
/// stands in for a mount point, so nothing is ever written outside the test's own temp directories.
/// </summary>
public class PerFilesystemStoreTests : IDisposable
{
    private static readonly GameInstallMetadataId InstallA = GameInstallMetadataId.From(EntityId.From(0xA1));
    private static readonly GameInstallMetadataId InstallB = GameInstallMetadataId.From(EntityId.From(0xB2));

    private readonly IFileSystem _fileSystem = FileSystem.Shared;
    private readonly AbsolutePath _temp;
    private readonly FakeFileStoreContents _contents = new();
    private readonly FakeFilesystems _filesystems = new();
    private readonly CapturingLogger _logger = new();

    public PerFilesystemStoreTests()
    {
        _temp = _fileSystem.GetKnownPath(KnownPath.TempDirectory).Combine($"per-fs-staging-{Guid.NewGuid():N}");
        _temp.CreateDirectory();
        _filesystems.Add(_temp.Combine("home"), device: 1, root: _temp.Combine("home"));
    }

    public void Dispose() => _temp.DeleteDirectory(recursive: true);

    private AbsolutePath DefaultLocation => _temp.Combine("home/data/LooseStaging");

    private LooseStagingStore NewStore(bool perFilesystem = true)
    {
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<LooseStagingStoreSettings>().Returns(new LooseStagingStoreSettings
        {
            Location = new ConfigurablePath(null, DefaultLocation.ToString()),
            PerFilesystemStores = perFilesystem,
        });
        return new LooseStagingStore(settings, FakeFileStoreContents.CreateFileStore(_contents), _fileSystem, _filesystems, _logger);
    }

    // A second filesystem whose root is a directory under the test's temp directory.
    private AbsolutePath AddFilesystem(string name, ulong device)
    {
        var root = _temp.Combine(name);
        root.CreateDirectory();
        _filesystems.Add(root, device, root);
        return root;
    }

    private Hash Add(ulong value, string content = "x")
    {
        var hash = Hash.From(value);
        _contents.Files[hash] = System.Text.Encoding.UTF8.GetBytes(content);
        return hash;
    }

    // Per user, so two accounts sharing a mount never share (or can plant) a store.
    private static readonly string UserFolder = $".silvermodmanager-{LibcUser.EffectiveUserId}";
    private static readonly string StoreFolder = $"{UserFolder}/staging";

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [Fact]
    public void ForRoot_RootOnTheDefaultFilesystem_IsTheDefaultStore()
    {
        var store = NewStore();
        _temp.Combine("home/games/Game").CreateDirectory();

        store.ForRoot(_temp.Combine("home/games/Game")).Should().BeSameAs(store);
    }

    [Fact]
    public void ForRoot_RootOnAnotherFilesystem_GetsAStoreAtThatFilesystemsRoot()
    {
        var steam = AddFilesystem("steam", device: 2);
        var store = NewStore();

        var other = store.ForRoot(steam.Combine("steamapps/common/Game"));

        other.Should().NotBeSameAs(store);
        other.Root.Should().Be(steam.Combine(StoreFolder));
        other.Root.DirectoryExists().Should().BeTrue();
    }

    [Fact]
    public void ForRoot_TwoRootsOnOneOtherFilesystem_ShareOneStore()
    {
        var steam = AddFilesystem("steam", device: 2);
        var store = NewStore();

        store.ForRoot(steam.Combine("a")).Should().BeSameAs(store.ForRoot(steam.Combine("b/c")));
    }

    [Fact]
    public void ForRoot_TwoOtherFilesystems_GetSeparateStores()
    {
        var one = AddFilesystem("one", device: 2);
        var two = AddFilesystem("two", device: 3);
        var store = NewStore();

        store.ForRoot(one).Should().NotBeSameAs(store.ForRoot(two));
    }

    [Fact]
    public void ForRoot_FromAPerFilesystemStore_AsksTheSameRegistry()
    {
        var steam = AddFilesystem("steam", device: 2);
        var store = NewStore();
        var other = store.ForRoot(steam);

        other.ForRoot(_temp.Combine("home")).Should().BeSameAs(store);
        other.ForRoot(steam.Combine("x")).Should().BeSameAs(other);
    }

    [Fact]
    public void ForRoot_PerFilesystemStoresOff_IsAlwaysTheDefaultStore()
    {
        var steam = AddFilesystem("steam", device: 2);
        var store = NewStore(perFilesystem: false);

        store.ForRoot(steam).Should().BeSameAs(store);
        steam.Combine(UserFolder).DirectoryExists().Should().BeFalse();
    }

    [Fact]
    public void ForRoot_FilesystemThatCannotHoldAStore_FallsBackAndLogsOnce()
    {
        // A file where the user's store folder would go: creating the store folder fails the way a
        // read-only mount does, without depending on permissions.
        var steam = AddFilesystem("steam", device: 2);
        File.WriteAllText(steam.Combine(UserFolder).ToString(), "in the way");
        var store = NewStore();

        store.ForRoot(steam.Combine("a")).Should().BeSameAs(store);
        store.ForRoot(steam.Combine("b")).Should().BeSameAs(store);

        _logger.Warnings.Should().ContainSingle();
    }

    [Fact]
    public void ForRoot_FilesystemRootThatIsUnknown_FallsBack()
    {
        var store = NewStore();

        store.ForRoot(_temp.Combine("nowhere/at/all")).Should().BeSameAs(store);
    }

    [Fact]
    public async Task ForRoot_StoreFolderFromAnEarlierRun_IsReused()
    {
        var steam = AddFilesystem("steam", device: 2);
        var hash = Add(0x11);
        var first = NewStore().ForRoot(steam);
        var path = await first.Ensure(InstallA, hash, TestContext.Current.CancellationToken);

        var again = NewStore().ForRoot(steam);

        again.Root.Should().Be(first.Root);
        again.Contains(InstallA, hash).Should().BeTrue();
        again.PathFor(InstallA, hash).Should().Be(path);
    }

    [Fact]
    public async Task Ensure_InAPerFilesystemStore_StagesOnThatFilesystem()
    {
        var steam = AddFilesystem("steam", device: 2);
        var store = NewStore();
        var other = store.ForRoot(steam);

        var path = await other.Ensure(InstallA, Add(0x12), TestContext.Current.CancellationToken);

        path.InFolder(steam).Should().BeTrue();
        store.Installations().Should().BeEmpty();
    }

    private static readonly Dictionary<GameInstallMetadataId, IReadOnlySet<Hash>> Nothing = new();

    [Fact]
    public async Task Sweep_KeepsLiveAndDropsDeadFilesInEveryStore()
    {
        var steam = AddFilesystem("steam", device: 2);
        var (liveHome, deadHome, liveSteam, deadSteam) = (Add(0x21), Add(0x22), Add(0x23), Add(0x24));
        var store = NewStore();
        var other = store.ForRoot(steam);
        var token = TestContext.Current.CancellationToken;
        await store.Ensure(InstallA, liveHome, token);
        await store.Ensure(InstallA, deadHome, token);
        await other.Ensure(InstallA, liveSteam, token);
        await other.Ensure(InstallA, deadSteam, token);
        await other.Ensure(InstallB, deadSteam, token);

        var deleted = store.Sweep(() => new Dictionary<GameInstallMetadataId, IReadOnlySet<Hash>>
        {
            [InstallA] = new HashSet<Hash> { liveHome, liveSteam },
        });

        deleted.Should().Be(3);
        store.Contains(InstallA, liveHome).Should().BeTrue();
        store.Contains(InstallA, deadHome).Should().BeFalse();
        other.Contains(InstallA, liveSteam).Should().BeTrue();
        other.Contains(InstallA, deadSteam).Should().BeFalse();
        other.Installations().Should().BeEquivalentTo([InstallA]);
    }

    [Fact]
    public async Task Sweep_LivenessPerStore_DropsTheCopyInAStoreTheFileNoLongerLinksFrom()
    {
        var (one, two) = (AddFilesystem("one", device: 2), AddFilesystem("two", device: 3));
        var hash = Add(0x61);
        var store = NewStore();
        var (stale, current) = (store.ForRoot(one), store.ForRoot(two));
        var token = TestContext.Current.CancellationToken;
        await stale.Ensure(InstallA, hash, token);
        await current.Ensure(InstallA, hash, token);

        var deleted = store.Sweep(() =>
        {
            var liveness = new StagingLiveness();
            liveness.Add(current.Root, InstallA, hash);
            return liveness;
        });

        deleted.Should().Be(1);
        current.Contains(InstallA, hash).Should().BeTrue();
        stale.Contains(InstallA, hash).Should().BeFalse();
        stale.Installations().Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_LivenessEverywhere_KeepsTheHashInEveryStore()
    {
        var steam = AddFilesystem("steam", device: 2);
        var hash = Add(0x62);
        var store = NewStore();
        var other = store.ForRoot(steam);
        var token = TestContext.Current.CancellationToken;
        await store.Ensure(InstallA, hash, token);
        await other.Ensure(InstallA, hash, token);

        var deleted = store.Sweep(() =>
        {
            var liveness = new StagingLiveness();
            liveness.AddEverywhere(InstallA, hash);
            return liveness;
        });

        deleted.Should().Be(0);
        store.Contains(InstallA, hash).Should().BeTrue();
        other.Contains(InstallA, hash).Should().BeTrue();
    }

    [Fact]
    public async Task Sweep_LivenessPerStore_StoreNobodyMentionsLosesEverything()
    {
        var (one, two) = (AddFilesystem("one", device: 2), AddFilesystem("two", device: 3));
        var hash = Add(0x63);
        var store = NewStore();
        var token = TestContext.Current.CancellationToken;
        await store.ForRoot(one).Ensure(InstallA, hash, token);
        await store.Ensure(InstallA, hash, token);

        var deleted = store.Sweep(() =>
        {
            var liveness = new StagingLiveness();
            liveness.Add(store.ForRoot(two).Root, InstallA, hash);
            return liveness;
        });

        deleted.Should().Be(2);
        store.Installations().Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_ReachesAStoreCreatedByAnEarlierRun()
    {
        var steam = AddFilesystem("steam", device: 2);
        var dead = Add(0x31);
        var earlier = NewStore();
        var path = await earlier.ForRoot(steam).Ensure(InstallA, dead, TestContext.Current.CancellationToken);

        // A new process knows no roots yet; the store on the other filesystem has to be found anyway.
        NewStore().Sweep(() => Nothing).Should().Be(1);

        path.FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task Sweep_FilesystemThatIsNotMounted_IsSkippedWithoutDeletingAnything()
    {
        var steam = AddFilesystem("steam", device: 2);
        var dead = Add(0x41);
        var path = await NewStore().ForRoot(steam).Ensure(InstallA, dead, TestContext.Current.CancellationToken);

        // Unmounted: the mount point is an empty folder again.
        var away = _temp.Combine("steam-unmounted-away");
        Directory.Move(steam.Combine(UserFolder).ToString(), away.ToString());
        var store = NewStore();
        var deleted = store.Sweep(() => Nothing);
        Directory.Move(away.ToString(), steam.Combine(UserFolder).ToString());

        deleted.Should().Be(0);
        path.FileExists.Should().BeTrue();
        _logger.Infos.Should().Contain(m => m.Contains("not mounted"));
    }

    [Fact]
    public async Task Hold_OnAPerFilesystemStore_BlocksTheSharedSweep()
    {
        var steam = AddFilesystem("steam", device: 2);
        var store = NewStore();
        var other = store.ForRoot(steam);
        await other.Ensure(InstallA, Add(0x51), TestContext.Current.CancellationToken);

        var hold = other.Hold();
        var sweep = Task.Run(() => store.Sweep(() => Nothing), TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        sweep.IsCompleted.Should().BeFalse();

        hold.Dispose();
        (await sweep.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public void ForRoot_CreatesTheStoreFoldersForTheUserAlone()
    {
        var steam = AddFilesystem("steam", device: 2);

        NewStore().ForRoot(steam);

        File.GetUnixFileMode(steam.Combine(UserFolder).ToString()).Should().Be(OwnerOnly);
        File.GetUnixFileMode(steam.Combine(StoreFolder).ToString()).Should().Be(OwnerOnly);
    }

    [Fact]
    public void ForRoot_StoreFolderThatIsASymlink_FallsBackAndLeavesTheTargetAlone()
    {
        // Someone else on a shared mount could plant a link that points the store at a folder they control.
        var steam = AddFilesystem("steam", device: 2);
        var elsewhere = _temp.Combine("elsewhere");
        elsewhere.CreateDirectory();
        File.CreateSymbolicLink(steam.Combine(UserFolder).ToString(), elsewhere.ToString());
        var store = NewStore();

        store.ForRoot(steam).Should().BeSameAs(store);

        Directory.EnumerateFileSystemEntries(elsewhere.ToString()).Should().BeEmpty();
        _logger.Warnings.Should().ContainSingle();
    }

    [Fact]
    public void ForRoot_StoreFolderOthersCanWrite_FallsBack()
    {
        var steam = AddFilesystem("steam", device: 2);
        var folder = steam.Combine(UserFolder).ToString();
        Directory.CreateDirectory(folder);
        File.SetUnixFileMode(folder, OwnerOnly | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        var store = NewStore();

        store.ForRoot(steam).Should().BeSameAs(store);

        _logger.Warnings.Should().ContainSingle();
    }

    [Fact]
    public async Task Sweep_RecordedStoreReplacedByASymlink_IsSkippedAndNothingBehindTheLinkIsDeleted()
    {
        var steam = AddFilesystem("steam", device: 2);
        var dead = Add(0x51);
        await NewStore().ForRoot(steam).Ensure(InstallA, dead, TestContext.Current.CancellationToken);

        // The recorded store folder is swapped for a link to a folder holding the same layout.
        var decoy = _temp.Combine("decoy");
        Directory.Move(steam.Combine(StoreFolder).ToString(), decoy.ToString());
        File.CreateSymbolicLink(steam.Combine(StoreFolder).ToString(), decoy.ToString());
        var before = Directory.EnumerateFiles(decoy.ToString(), "*", SearchOption.AllDirectories).Count();

        var deleted = NewStore().Sweep(() => Nothing);

        deleted.Should().Be(0);
        Directory.EnumerateFiles(decoy.ToString(), "*", SearchOption.AllDirectories).Count().Should().Be(before);
        _logger.Warnings.Should().ContainSingle();
    }

    [Fact]
    public void Settings_PerFilesystemStoresAreOnByDefault()
    {
        LooseStagingStoreSettings.CreateDefault(null!).PerFilesystemStores.Should().BeTrue();
    }

    // Records what the store logs, because "one line, then fall back" is part of the contract.
    private sealed class CapturingLogger : ILogger<LooseStagingStore>
    {
        public List<string> Warnings { get; } = [];
        public List<string> Infos { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
            else if (logLevel == LogLevel.Information) Infos.Add(formatter(state, exception));
        }
    }

    // Longest registered prefix wins, like mounts nest.
    private sealed class FakeFilesystems : IFilesystemLocator
    {
        private readonly List<(AbsolutePath Prefix, ulong Device, AbsolutePath Root)> _entries = [];

        public void Add(AbsolutePath prefix, ulong device, AbsolutePath root) => _entries.Add((prefix, device, root));

        private (AbsolutePath Prefix, ulong Device, AbsolutePath Root)? Find(AbsolutePath path)
        {
            (AbsolutePath, ulong, AbsolutePath)? best = null;
            foreach (var entry in _entries)
            {
                if (path != entry.Prefix && !path.InFolder(entry.Prefix)) continue;
                if (best is null || entry.Prefix.ToString().Length > best.Value.Item1.ToString().Length) best = entry;
            }
            return best;
        }

        public ulong? DeviceOf(AbsolutePath path) => Find(path)?.Device;

        public AbsolutePath? RootOf(AbsolutePath path) => Find(path)?.Root;
    }
}
