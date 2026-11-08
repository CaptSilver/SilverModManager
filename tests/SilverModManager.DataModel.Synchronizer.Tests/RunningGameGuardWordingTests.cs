using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

public class RunningGameGuardWordingTests(ITestOutputHelper helper) : AStubbedGameTest<RunningGameGuardWordingTests>(helper)
{
    private FakeGameLaunchSession Session => ServiceProvider.GetRequiredService<FakeGameLaunchSession>();

    [Fact]
    public async Task WhileAFinishHoldsTheGame_ApplyIsRefusedWithTheFinishingLine()
    {
        var loadout = await CreateLoadout();
        Session.Running = true;
        Session.Finishing = true;

        var act = async () => await SynchronizerService.Synchronize(loadout.LoadoutId);

        var thrown = (await act.Should().ThrowAsync<ExecutableInUseException>()).Which;
        thrown.Message.Should().Be($"{loadout.InstallationInstance.Game.DisplayName} is being finished after its last session in SilverModManager. Wait a moment, then try again.");
    }

    [Fact]
    public async Task WhileAGameRuns_ApplyIsRefusedWithTheRunningLine()
    {
        var loadout = await CreateLoadout();
        Session.Running = true;
        Session.Finishing = false;

        var act = async () => await SynchronizerService.Synchronize(loadout.LoadoutId);

        var thrown = (await act.Should().ThrowAsync<ExecutableInUseException>()).Which;
        thrown.Message.Should().Contain("is running from SilverModManager");
    }
}
