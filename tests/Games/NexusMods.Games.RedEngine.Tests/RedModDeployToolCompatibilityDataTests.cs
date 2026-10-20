using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Loadouts;
using NexusMods.App.UI.DiagnosticSystem;
using NexusMods.Backend;
using NexusMods.Games.Generic;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.RedEngine.Cyberpunk2077.Emitters;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;
using Xunit;

namespace NexusMods.Games.RedEngine.Tests;

/// <summary>
/// RedMod deploys by running redmod through the tool runner, so the tool has to hand it the right
/// script, arguments and prefix, and fail loudly when nothing can run it.
/// </summary>
public class RedModDeployToolCompatibilityDataTests : AIsolatedGameTest<RedModDeployToolCompatibilityDataTests, Cyberpunk2077Game>
{
    public RedModDeployToolCompatibilityDataTests(ITestOutputHelper helper) : base(helper) { }

    // Steam locator IDs are numeric manifest IDs, and the sync tree query casts them to integers.
    // The stubbed file hasher only knows this one for Steam.
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddDiagnosticWriter()
            .AddOSInterop()
            .AddRuntimeDependencies()
            .AddGenericGameSupport()
            .AddFakeToolRunner()
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<Cyberpunk2077Game>(
                provider,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.Steam]
            )
            {
                LocatorIds = [LocatorId.From("3976631895")],
            })
            .AddRedEngineGames();
    }

    private RedModDeployTool Tool => ServiceProvider.GetServices<ITool>().OfType<RedModDeployTool>().Single();
    private FakeToolRunner ToolRunner => ServiceProvider.GetRequiredService<FakeToolRunner>();
    private FakePrefixResolver PrefixResolver => ServiceProvider.GetRequiredService<FakePrefixResolver>();

    [Fact]
    public async Task Execute_SteamLoadout_RunsTheExtractedScriptWithTheDeployArguments()
    {
        var loadout = await CreateLoadout();

        await Tool.Execute(loadout, CancellationToken.None);

        var call = ToolRunner.Calls.Should().ContainSingle().Which;
        call.Installation.Should().Be(loadout.InstallationInstance);
        call.Exe.Extension.Should().Be(new Extension(".bat"));
        call.ExeContents.Should().Contain("redMod.exe %*");
        call.Args.Should().Equal("deploy", "-force", "-modlist=C:\\modlist.txt");
        call.LogOutput.Should().BeTrue();
    }

    [Fact]
    public async Task Execute_SteamLoadout_TellsTheScriptWhereTheGameLives()
    {
        var loadout = await CreateLoadout();

        await Tool.Execute(loadout, CancellationToken.None);

        var gameFolder = loadout.InstallationInstance.Locations[LocationId.Game].Path;
        ToolRunner.Calls.Single().Env.Should().Contain("STEAM_APP_PATH", gameFolder.ToString());
    }

    [Fact]
    public async Task Execute_WritesModlistIntoTheResolvedPrefixDriveC()
    {
        var loadout = await CreateLoadout();

        await Tool.Execute(loadout, CancellationToken.None);

        PrefixResolver.DefaultPrefix.Path.Combine("drive_c/modlist.txt").FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task Execute_ToolRunnerCannotRun_ThrowsNamingTheGameAndTheReason()
    {
        var loadout = await CreateLoadout();
        ToolRunner.CanRunTools = false;
        ToolRunner.Diagnostic = "No Proton build is installed.";

        var act = async () => await Tool.Execute(loadout, CancellationToken.None);

        var exception = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        exception.Message.Should().Contain(Cyberpunk2077Game.DisplayName);
        exception.Message.Should().Contain("No Proton build is installed.");
        ToolRunner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_PrefixCannotBeResolved_ThrowsNamingTheGameAndTheDiagnostic()
    {
        var loadout = await CreateLoadout();
        PrefixResolver.Resolution = _ => PrefixResolution.Failed("Two different prefixes were found.");

        var act = async () => await Tool.Execute(loadout, CancellationToken.None);

        var exception = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        exception.Message.Should().Contain(Cyberpunk2077Game.DisplayName);
        exception.Message.Should().Contain("Two different prefixes were found.");
        ToolRunner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_RedModExitsWithAnError_ThrowsNamingTheGameAndTheExitCode()
    {
        var loadout = await CreateLoadout();
        ToolRunner.ExitCode = 1;

        var act = async () => await Tool.Execute(loadout, CancellationToken.None);

        var exception = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        exception.Message.Should().Contain(Cyberpunk2077Game.DisplayName);
        exception.Message.Should().Contain("exit code 1");
    }

    [Fact]
    public async Task Emitter_RedModInstalledAndToolRunnerCannotRun_ReportsTheReason()
    {
        var loadout = await CreateLoadout();
        InstallRedModExe(loadout);
        ToolRunner.CanRunTools = false;
        ToolRunner.Diagnostic = "No Proton build is installed.";

        var diagnostics = await new NoWayToRunRedModEmitter(ServiceProvider).Diagnose(loadout, CancellationToken.None).ToArrayAsync();

        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Title.Should().Be("No way to run REDmod");
        diagnostic.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>()).Should().Contain("No Proton build is installed.");
    }

    [Fact]
    public async Task Emitter_ToolRunnerCanRun_ReportsNothing()
    {
        var loadout = await CreateLoadout();
        InstallRedModExe(loadout);

        var diagnostics = await new NoWayToRunRedModEmitter(ServiceProvider).Diagnose(loadout, CancellationToken.None).ToArrayAsync();

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task Emitter_RedModNotInstalled_ReportsNothing()
    {
        var loadout = await CreateLoadout();
        ToolRunner.CanRunTools = false;

        var diagnostics = await new NoWayToRunRedModEmitter(ServiceProvider).Diagnose(loadout, CancellationToken.None).ToArrayAsync();

        diagnostics.Should().BeEmpty("without the REDmod DLC there is nothing to run");
    }

    private static void InstallRedModExe(Loadout.ReadOnly loadout)
    {
        var exe = loadout.InstallationInstance.Locations[LocationId.Game].Path.Combine("tools/redmod/bin/redMod.exe");
        exe.Parent.CreateDirectory();
        File.WriteAllText(exe.ToString(), "stub");
    }
}
