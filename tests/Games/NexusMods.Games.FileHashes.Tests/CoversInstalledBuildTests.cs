using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.FileHashes.Tests;

/// <summary>
/// Whether the vanilla file list covers an installed build: every locator id has to resolve, because an id
/// no store knows contributes no files and which paths it holds is unknown.
/// </summary>
public sealed class CoversInstalledBuildTests : IDisposable
{
    private readonly ServiceHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task CoversInstalledBuildIsTrueOnlyWhenEveryLocatorIdResolves()
    {
        var service = await ServiceWithCentral(async c =>
        {
            await HashRows.AddSteamManifest(c, 1001, ("a.esm", "a"));
            await HashRows.AddSteamManifest(c, 1002, ("b.esm", "b"));
        });

        service.CoversInstalledBuild((GameStore.Steam, [LocatorId.From("1001"), LocatorId.From("1002")])).Should().BeTrue();
        service.CoversInstalledBuild((GameStore.Steam, [LocatorId.From("1001"), LocatorId.From("9999")])).Should().BeFalse("one depot is unknown");
        service.CoversInstalledBuild((GameStore.Steam, [LocatorId.From("9999")])).Should().BeFalse();
        service.CoversInstalledBuild((GameStore.Steam, [])).Should().BeFalse("nothing was asked about");
        service.CoversInstalledBuild((GameStore.Steam, [LocatorId.From("not-a-number")])).Should().BeFalse();
    }

    [Fact]
    public async Task CoversInstalledBuildForGogNeedsABuildAndOnlyProductsOfIt()
    {
        var service = await ServiceWithCentral(c => HashRows.AddGogBuild(c, 100, ("a.esm", "a")));

        service.CoversInstalledBuild((GameStore.GOG, [LocatorId.From("100")])).Should().BeTrue();
        service.CoversInstalledBuild((GameStore.GOG, [LocatorId.From("100"), LocatorId.From("10")])).Should().BeTrue("10 is the build's product");
        service.CoversInstalledBuild((GameStore.GOG, [LocatorId.From("100"), LocatorId.From("77")])).Should().BeFalse("77 belongs to no resolved build");
        service.CoversInstalledBuild((GameStore.GOG, [LocatorId.From("10")])).Should().BeFalse("no build resolves");
    }

    [Fact]
    public async Task CoversInstalledBuildCountsTheLocalStore()
    {
        var service = await ServiceWithCentral(c => HashRows.AddSteamManifest(c, 1001, ("a.esm", "a")));
        await HashRows.AddBaselineOwningManifest(service.Local.Connection, LocalBaselineKind.DepotManifests, 1002, ("b.esm", "b"));
        await HashRows.AddBaselineOwningManifest(service.Local.Connection, LocalBaselineKind.PartialDepotManifests, 1003, ("c.esm", "c"));

        service.CoversInstalledBuild((GameStore.Steam, [LocatorId.From("1001"), LocatorId.From("1002")])).Should().BeTrue();
        service.CoversInstalledBuild((GameStore.Steam, [LocatorId.From("1003")])).Should().BeTrue("a partial baseline covers the depots it read");
        service.CoversInstalledBuild((GameStore.Steam, [LocatorId.From("1003"), LocatorId.From("1004")])).Should().BeFalse("the depot it did not read is unknown");
    }

    [Fact]
    public async Task CoversInstalledBuildNeverCountsASnapshotOrABaselineThatClassifiedNothing()
    {
        var service = await ServiceWithCentral(c => HashRows.AddSteamManifest(c, 1001, ("a.esm", "a")));
        await HashRows.AddBaselineOwningManifest(service.Local.Connection, LocalBaselineKind.Snapshot, 1002, ("b.esm", "b"));
        await HashRows.AddBaselineOwningManifest(service.Local.Connection, LocalBaselineKind.DepotManifests, 1003);

        service.CoversInstalledBuild((GameStore.Steam, [LocatorId.From("1002")])).Should().BeFalse("a snapshot is whatever was on disk");
        service.CoversInstalledBuild((GameStore.Steam, [LocatorId.From("1003")])).Should().BeFalse("zero files were classified");
    }

    [Fact]
    public async Task CoversInstalledBuildDoesNotCountALocalManifestNoBaselineOwns()
    {
        var service = await ServiceWithCentral(c => HashRows.AddSteamManifest(c, 1001, ("a.esm", "a")));
        await HashRows.AddSteamManifest(service.Local.Connection, 1002, ("b.esm", "b"));

        service.CoversInstalledBuild((GameStore.Steam, [LocatorId.From("1002")])).Should().BeFalse();
    }

    [Fact]
    public async Task AmazonInstallsAreNotCoveredAndHaveNoFileList()
    {
        var service = await ServiceWithCentral(c => HashRows.AddSteamManifest(c, 1001, ("a.esm", "a")));
        LocatorIdsWithGameStore amazon = (GameStore.Amazon, [LocatorId.From("1.2.3")]);

        service.CoversInstalledBuild(amazon).Should().BeFalse("no store data describes an Amazon build");
        service.Invoking(s => s.GetGameFiles(amazon).ToArray()).Should().Throw<NotSupportedException>();
    }

    private async Task<FileHashesService> ServiceWithCentral(Func<IConnection, Task> fill)
    {
        var location = _harness.NewDatabaseLocation();
        await _harness.CreateDatabaseOnDisk(location, 1000, fill);
        var service = _harness.CreateService(location);
        await service.CheckForUpdate();
        return service;
    }
}
