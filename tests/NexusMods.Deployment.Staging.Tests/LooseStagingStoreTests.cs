using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Settings;
using NSubstitute;

namespace NexusMods.Deployment.Staging.Tests;

public class LooseStagingStoreTests
{
    private static readonly GameInstallMetadataId InstallA = GameInstallMetadataId.From(EntityId.From(0xA1));
    private static readonly GameInstallMetadataId InstallB = GameInstallMetadataId.From(EntityId.From(0xB2));

    private readonly LooseStagingStore _store;
    private readonly FakeFileStoreContents _contents;
    private readonly IFileStore _fileStore;
    private readonly IServiceProvider _serviceProvider;

    // Each test gets its own store over a fresh directory, so tests stay independent of one another.
    public LooseStagingStoreTests(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _contents = new FakeFileStoreContents();
        _fileStore = FakeFileStoreContents.CreateFileStore(_contents);
        var fileSystem = serviceProvider.GetRequiredService<IFileSystem>();
        var location = fileSystem.GetKnownPath(KnownPath.TempDirectory).Combine($"loose-staging-{Guid.NewGuid():N}");
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<LooseStagingStoreSettings>().Returns(new LooseStagingStoreSettings
        {
            Location = new ConfigurablePath(null, location.ToString()),
        });
        _store = new LooseStagingStore(settings, _fileStore, fileSystem, Microsoft.Extensions.Logging.Abstractions.NullLogger<LooseStagingStore>.Instance);
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

        var first = await _store.Ensure(InstallA, hash);
        var second = await _store.Ensure(InstallA, hash);

        second.Should().Be(first);
        first.FileExists.Should().BeTrue();
        _contents.ExtractedTo.Should().HaveCount(1);
        _store.Enumerate(InstallA).Should().HaveCount(1);
    }

    [Fact]
    public async Task Ensure_SameHashForTwoInstallations_StagesTwoFiles()
    {
        var hash = Add(0xab, 0x02);

        var a = await _store.Ensure(InstallA, hash);
        var b = await _store.Ensure(InstallB, hash);

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
        await stale.WriteAllTextAsync("half a file");

        var path = await _store.Ensure(InstallA, hash);

        (await path.ReadAllTextAsync()).Should().Be("fresh");
        stale.FileExists.Should().BeFalse();
        path.Parent.EnumerateFiles().Should().ContainSingle().Which.Should().Be(path);
    }

    [Fact]
    public async Task Contains_ReflectsWhetherTheFileIsStaged()
    {
        var hash = Add(0xab, 0x04);

        _store.Contains(InstallA, hash).Should().BeFalse();
        await _store.Ensure(InstallA, hash);
        _store.Contains(InstallA, hash).Should().BeTrue();
        _store.Contains(InstallB, hash).Should().BeFalse();
    }

    [Fact]
    public async Task EnumerateAndInstallations_ListWhatIsStaged()
    {
        var h1 = Add(0xab, 0x05);
        var h2 = Add(0xcd, 0x06);
        await _store.Ensure(InstallA, h1);
        await _store.Ensure(InstallA, h2);
        await _store.Ensure(InstallB, h1);

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
        await _store.Ensure(InstallA, h1);
        await _store.Ensure(InstallA, h2);
        await _store.Ensure(InstallB, h3);

        var deleted = _store.Sweep(new Dictionary<GameInstallMetadataId, IReadOnlySet<Hash>>
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

        _store.Sweep(new Dictionary<GameInstallMetadataId, IReadOnlySet<Hash>>()).Should().Be(0);
        _store.Enumerate(InstallA).Should().BeEmpty();
        _store.Installations().Should().BeEmpty();
        _store.Contains(InstallA, Hash.From(1)).Should().BeFalse();

        _store.Root.DirectoryExists().Should().BeFalse();
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
