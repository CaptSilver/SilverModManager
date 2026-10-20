using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.TestFramework;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests;

/// <summary>
/// Whether the script extender loader is the thing to run is decided by the loadout, not by what
/// happens to be on disk: a stale loader left over from an earlier deploy must not be started.
/// </summary>
public class RunGameViaScriptExtenderToolTests(ITestOutputHelper helper) : AIsolatedGameTest<RunGameViaScriptExtenderToolTests, CreationEngine.SkyrimSE.SkyrimSE>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddCreationEngine()
            .AddUniversalGameLocator<CreationEngine.SkyrimSE.SkyrimSE>(new Version("1.6.1170"));
    }

    private RunGameTool<CreationEngine.SkyrimSE.SkyrimSE> Tool => ServiceProvider.GetServices<ITool>()
        .OfType<RunGameViaScriptExtenderTool<CreationEngine.SkyrimSE.SkyrimSE>>().Single();

    private AbsolutePath LoaderOnDisk => GameInstallation.Locations.ToAbsolutePath(KnownPaths.SKSE64Loader);

    private AbsolutePath PrimaryExe => GameInstallation.Locations.ToAbsolutePath(Game.GetPrimaryFile(GameInstallation));

    [Fact]
    public async Task LoaderInTheLoadoutButNotOnDisk_ResolvesToTheLoader()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout, "SKSE");
            AddFile(tx, loadout, group, KnownPaths.SKSE64Loader);
            await tx.Commit();
        }
        Refresh(ref loadout);
        LoaderOnDisk.FileExists.Should().BeFalse();

        var spec = await Tool.GetLaunchSpec(loadout, CancellationToken.None);

        spec.Exe.Should().Be(LoaderOnDisk);
        spec.ExtraArgs.Should().BeEmpty();
    }

    [Fact]
    public async Task LoaderAbsentFromTheLoadoutDespiteAStaleFile_ResolvesToThePrimaryExecutable()
    {
        var loadout = await CreateLoadout();
        LoaderOnDisk.Parent.CreateDirectory();
        await LoaderOnDisk.WriteAllBytesAsync("stale"u8.ToArray());

        var spec = await Tool.GetLaunchSpec(loadout, CancellationToken.None);

        spec.Exe.Should().Be(PrimaryExe);
    }

    [Fact]
    public async Task LoaderOnlyRecordedAsDeleted_ResolvesToThePrimaryExecutable()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            var overrides = AddEmptyGroup(tx, loadout, "Overrides");
            AddDeletedFile(tx, loadout, overrides, KnownPaths.SKSE64Loader);
            await tx.Commit();
        }
        Refresh(ref loadout);

        var spec = await Tool.GetLaunchSpec(loadout, CancellationToken.None);

        spec.Exe.Should().Be(PrimaryExe);
    }

    [Fact]
    public async Task LoaderFromAModButRecordedAsDeleted_ResolvesToThePrimaryExecutable()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            var mod = AddEmptyGroup(tx, loadout, "SKSE");
            AddFile(tx, loadout, mod, KnownPaths.SKSE64Loader);
            var overrides = AddEmptyGroup(tx, loadout, "Overrides");
            AddDeletedFile(tx, loadout, overrides, KnownPaths.SKSE64Loader);
            await tx.Commit();
        }
        Refresh(ref loadout);

        var spec = await Tool.GetLaunchSpec(loadout, CancellationToken.None);

        spec.Exe.Should().Be(PrimaryExe);
    }

    private static void AddDeletedFile(ITransaction tx, LoadoutId loadoutId, LoadoutItemGroupId groupId, GamePath path)
    {
        _ = new DeletedFile.New(tx, out var id)
        {
            Reason = "Deleted from disk",
            LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, id)
            {
                TargetPath = path.ToGamePathParentTuple(loadoutId),
                LoadoutItem = new LoadoutItem.New(tx, id)
                {
                    LoadoutId = loadoutId,
                    ParentId = groupId,
                    Name = path.Path,
                },
            },
        };
    }
}
