using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Values;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.App.UI.DiagnosticSystem;
using SilverModManager.Backend;
using SilverModManager.Games.MountAndBlade2Bannerlord.Diagnostics;
using SilverModManager.Games.Generic;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;
using NSubstitute;
using Xunit;
using static Bannerlord.LauncherManager.Constants;

namespace SilverModManager.Games.MountAndBlade2Bannerlord.Tests;

public class BannerlordRunGameToolTests(ITestOutputHelper helper) : AIsolatedGameTest<BannerlordRunGameToolTests, Bannerlord>(helper)
{
    private readonly CapturingLogger _toolLogger = new();

    private static readonly GamePath BlseGamePath = new(LocationId.Game, (RelativePath)BinFolder / Win64Configuration / LoadoutExtensions.BlseExecutable);

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        // Nothing may start a process or reach the desktop.
        return base.AddServices(services)
            .AddFakeToolRunner()
            .AddDiagnosticWriter()
            .AddOSInterop()
            .AddRuntimeDependencies()
            .RemoveRuntimeDependencyChecker()
            .AddGenericGameSupport()
            .AddMountAndBlade2Bannerlord()
            .AddUniversalGameLocator<Bannerlord>(new Version("1.0"))
            .AddSingleton(Substitute.For<IOSInterop>())
            .AddSingleton<ILogger<BannerlordRunGameTool>>(_toolLogger);
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

    private FakeToolRunner ToolRunner => ServiceProvider.GetRequiredService<FakeToolRunner>();

    [Fact]
    public async Task Execute_WithoutBlse_HandsTheSpecToTheToolRunnerAfterTheCallersArguments()
    {
        var loadout = await CreateLoadout();
        var spec = await Tool.GetLaunchSpec(loadout, CancellationToken.None);

        await Tool.Execute(loadout, CancellationToken.None, ["-caller"]);

        var call = ToolRunner.Calls.Should().ContainSingle().Which;
        call.Installation.Should().Be(loadout.InstallationInstance);
        call.Exe.Should().Be(spec.Exe);
        call.Args.Should().Equal(new[] { "-caller" }.Concat(spec.ExtraArgs));
        call.Args[2].Should().StartWith("_MODULES_*").And.EndWith("*_MODULES_");
        call.LogOutput.Should().BeTrue();
    }

    [Fact]
    public async Task Execute_WithBlse_RunsBlseWithForcedNetCore()
    {
        var loadout = await CreateLoadout();
        await InstallBlse(loadout);
        Refresh(ref loadout);

        await Tool.Execute(loadout, CancellationToken.None, null);

        var call = ToolRunner.Calls.Should().ContainSingle().Which;
        call.Exe.Should().Be(GameInstallation.Locations.ToAbsolutePath(BlseGamePath));
        call.Args[0].Should().Be("/singleplayer");
        call.Args[1].Should().StartWith("_MODULES_*");
        call.Args[^1].Should().Be("/forcenetcore");
    }

    [Fact]
    public async Task Execute_NonZeroExit_LogsAWarningWithTheExitCode()
    {
        var loadout = await CreateLoadout();
        ToolRunner.ExitCode = 3;

        await Tool.Execute(loadout, CancellationToken.None, null);

        var exe = ToolRunner.Calls.Should().ContainSingle().Which.Exe;
        _toolLogger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("(3)").And.Contain(exe.ToString());
    }

    [Fact]
    public async Task Execute_ToolRunnerCannotRun_Throws()
    {
        var loadout = await CreateLoadout();
        ToolRunner.CanRunTools = false;
        ToolRunner.Diagnostic = "No Proton build is installed.";

        var act = async () => await Tool.Execute(loadout, CancellationToken.None, null);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("No Proton build is installed.");
        ToolRunner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Emitter_ToolRunnerCannotRun_ReportsTheReason()
    {
        var loadout = await CreateLoadout();
        ToolRunner.CanRunTools = false;
        ToolRunner.Diagnostic = "No Proton build is installed.";

        var diagnostics = await new NoWayToRunBannerlordEmitter(ServiceProvider).Diagnose(loadout, CancellationToken.None).ToArrayAsync();

        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Title.Should().Be("No way to run Bannerlord");
        diagnostic.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>()).Should().Contain("No Proton build is installed.");
        diagnostic.Id.Should().NotBe(Diagnostics.Diagnostics.CreateMissingBLSE("Mod", "Mod", "Bannerlord.BLSE", new NamedLink("BLSE", new Uri("https://example.invalid"))).Id);
    }

    [Fact]
    public async Task Emitter_ToolRunnerCanRun_ReportsNothing()
    {
        var loadout = await CreateLoadout();

        var diagnostics = await new NoWayToRunBannerlordEmitter(ServiceProvider).Diagnose(loadout, CancellationToken.None).ToArrayAsync();

        diagnostics.Should().BeEmpty();
    }

    private sealed class CapturingLogger : ILogger<BannerlordRunGameTool>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_entries) return _entries.ToArray(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
