using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Exceptions;
using NexusMods.DataModel.Synchronizer;
using NexusMods.Games.TestFramework;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using Loadout = NexusMods.Sdk.Loadouts.Loadout;

namespace NexusMods.DataModel.Tests;

public class RunningGameGuardTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<RunningGameGuardTests>(helper)
{
    private FakeGameLaunchSession LaunchSession => ServiceProvider.GetRequiredService<FakeGameLaunchSession>();

    private bool IsVisible(LoadoutId id)
    {
        var loadout = Loadout.Load(Connection.Db, id);
        return loadout.IsValid() && loadout.IsVisible();
    }

    private LoadoutId? LastSynced()
    {
        var metadata = GameInstallMetadata.Load(Connection.Db, GameRegistry.ForceGetMetadata(GameInstallation).Id);
        return GameInstallMetadata.LastSyncedLoadout.TryGetValue(metadata, out var id) ? LoadoutId.From(id) : null;
    }

    private string[] GameFolderFiles() => GameInstallation.Locations[LocationId.Game].Path
        .EnumerateFiles()
        .Select(f => f.ToString())
        .Order()
        .ToArray();

    [Fact]
    public async Task Synchronize_ThrowsWhileRunning_AndLeavesTheGameFolderAlone()
    {
        var active = await LoadoutManager.CreateLoadout(GameInstallation, "Active Loadout");
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, "Guarded Apply");
        LastSynced().Should().Be(active.LoadoutId, "the first loadout is the one on disk");
        var before = GameFolderFiles();
        LaunchSession.Running = true;

        var act = () => SynchronizerService.Synchronize(loadout.LoadoutId);

        var thrown = await act.Should().ThrowAsync<ExecutableInUseException>();
        thrown.Which.Message.Should().Contain(GameInstallation.Game.DisplayName).And.Contain("Guarded Apply");
        LastSynced().Should().Be(active.LoadoutId, "the game synchronizer must not run");
        GameFolderFiles().Should().Equal(before);
    }

    [Fact]
    public async Task Synchronize_RunsWhenNothingIsRunning()
    {
        var active = await LoadoutManager.CreateLoadout(GameInstallation, "Active Loadout");
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, "Unguarded Apply");

        await SynchronizerService.Synchronize(loadout.LoadoutId);

        LastSynced().Should().Be(loadout.LoadoutId);
    }

    [Fact]
    public async Task SynchronizeFromLaunchSession_IsNotRefusedWhileRunning()
    {
        var active = await LoadoutManager.CreateLoadout(GameInstallation, "Active Loadout");
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, "Session Apply");
        LaunchSession.Running = true;

        await ((SynchronizerService)SynchronizerService).SynchronizeFromLaunchSession(loadout.LoadoutId);

        LastSynced().Should().Be(loadout.LoadoutId);
    }

    [Fact]
    public async Task DeleteLoadout_ThrowsWhileRunning_BeforeTombstoning()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, "Guarded Delete");
        LaunchSession.Running = true;

        var act = async () => await LoadoutManager.DeleteLoadout(loadout.LoadoutId);

        await act.Should().ThrowAsync<ExecutableInUseException>();
        IsVisible(loadout.LoadoutId).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteLoadout_RunsWhenNothingIsRunning()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, "Unguarded Delete");

        await LoadoutManager.DeleteLoadout(loadout.LoadoutId);

        IsVisible(loadout.LoadoutId).Should().BeFalse();
    }

    [Fact]
    public async Task UnManage_ThrowsWhileRunning_WithEveryLoadoutVisibleAndTheFolderUntouched()
    {
        var first = await LoadoutManager.CreateLoadout(GameInstallation, "Guarded Unmanage A");
        var second = await LoadoutManager.CreateLoadout(GameInstallation, "Guarded Unmanage B");
        await SynchronizerService.Synchronize(first.LoadoutId);
        var before = GameFolderFiles();
        LaunchSession.Running = true;

        var act = async () => await LoadoutManager.UnManage(GameInstallation);

        await act.Should().ThrowAsync<ExecutableInUseException>();
        IsVisible(first.LoadoutId).Should().BeTrue();
        IsVisible(second.LoadoutId).Should().BeTrue();
        GameFolderFiles().Should().Equal(before);
    }

    [Fact]
    public async Task UnManage_RunsWhenNothingIsRunning()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, "Unguarded Unmanage");

        await LoadoutManager.UnManage(GameInstallation);

        IsVisible(loadout.LoadoutId).Should().BeFalse();
    }
}
