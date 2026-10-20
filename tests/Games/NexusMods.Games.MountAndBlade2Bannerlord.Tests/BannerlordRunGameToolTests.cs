using CliWrap;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Loadouts;
using NexusMods.Backend;
using NexusMods.Games.Generic;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.StandardGameLocators.TestHelpers;
using NSubstitute;
using Xunit;
using static Bannerlord.LauncherManager.Constants;

namespace NexusMods.Games.MountAndBlade2Bannerlord.Tests;

public class BannerlordRunGameToolTests(ITestOutputHelper helper) : AIsolatedGameTest<BannerlordRunGameToolTests, Bannerlord>(helper)
{
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();

    private static readonly GamePath BlseGamePath = new(LocationId.Game, (RelativePath)BinFolder / Win64Configuration / LoadoutExtensions.BlseExecutable);

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        // Nothing may start a process or reach the desktop.
        return base.AddServices(services)
            .AddOSInterop()
            .AddRuntimeDependencies()
            .AddGenericGameSupport()
            .AddMountAndBlade2Bannerlord()
            .AddUniversalGameLocator<Bannerlord>(new Version("1.0"))
            .AddSingleton(_processRunner)
            .AddSingleton(Substitute.For<IOSInterop>());
    }

    private BannerlordRunGameTool Tool => ServiceProvider.GetServices<ITool>().OfType<BannerlordRunGameTool>().Single();

    private async Task InstallBlse(Loadout.ReadOnly loadout)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout, "BLSE");
        AddFile(tx, loadout, group, BlseGamePath);
        await tx.Commit();
    }

    [Fact]
    public async Task WithoutBlse_LaunchSpecIsTheGameExecutable()
    {
        var loadout = await CreateLoadout();

        var spec = await Tool.GetLaunchSpec(loadout, CancellationToken.None);

        spec.Exe.Should().Be(GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, (RelativePath)BinFolder / Win64Configuration / BannerlordExecutable)));
        spec.ExtraArgs.Should().HaveCount(2);
        spec.ExtraArgs[0].Should().Be("/singleplayer");
        spec.ExtraArgs.Should().NotContain("/forcenetcore");
    }

    [Fact]
    public async Task ModuleList_KeepsItsStarDelimiters()
    {
        var loadout = await CreateLoadout();

        var spec = await Tool.GetLaunchSpec(loadout, CancellationToken.None);

        var moduleList = spec.ExtraArgs[1];
        moduleList.Should().StartWith("_MODULES_*").And.EndWith("*_MODULES_");
        moduleList.Should().Contain("*Native*");
    }

    [Fact]
    public async Task WithBlse_LaunchSpecIsBlseAndForcesNetCore()
    {
        var loadout = await CreateLoadout();
        await InstallBlse(loadout);
        Refresh(ref loadout);

        var spec = await Tool.GetLaunchSpec(loadout, CancellationToken.None);

        spec.Exe.Should().Be(GameInstallation.Locations.ToAbsolutePath(BlseGamePath));
        spec.ExtraArgs[0].Should().Be("/singleplayer");
        spec.ExtraArgs[1].Should().StartWith("_MODULES_*");
        spec.ExtraArgs[^1].Should().Be("/forcenetcore");
    }

    [Fact]
    public async Task Execute_StartsTheSpecAfterTheCallersArguments()
    {
        var loadout = await CreateLoadout();
        await InstallBlse(loadout);
        Refresh(ref loadout);
        var spec = await Tool.GetLaunchSpec(loadout, CancellationToken.None);

        await Tool.Execute(loadout, CancellationToken.None, ["-caller"]);

        var command = _processRunner.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IProcessRunner.RunAsync))
            .Select(c => c.GetArguments()[0])
            .OfType<Command>()
            .Where(c => c.TargetFilePath == spec.Exe.ToString())
            .Should().ContainSingle().Which;
        command.TargetFilePath.Should().Be(spec.Exe.ToString());
        command.Arguments.Should().Be(string.Join(' ', new[] { "-caller" }.Concat(spec.ExtraArgs)));
        command.WorkingDirPath.Should().Be(spec.Exe.Parent.ToString());
    }
}
