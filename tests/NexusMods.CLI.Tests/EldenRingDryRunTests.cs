using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.App;
using NexusMods.Games.FromSoft;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Overlay;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Platform.Proton;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Settings;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.CLI.Tests;

/// <summary>
/// <c>silvermod run &lt;loadout&gt; --dry-run</c> for an Elden Ring loadout with mods: prints the command that would
/// start, and starts nothing. The Steam command it is given is discarded except for the app id.
/// </summary>
[Collection("ConsoleOutput")]
public class EldenRingDryRunTests(ITestOutputHelper helper) : AIsolatedGameTest<EldenRingDryRunTests, EldenRingGame>(helper)
{
    private const string StubbedSteamId = "3976631895";

    private sealed class FakeBwrap : IExecutableLocator
    {
        public AbsolutePath? Find(string name) => NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath("/fake/bin/bwrap");
    }

    private sealed record FixedIds(uint Uid, uint Gid) : IUserIds;

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        var dataRoot = new ConfigurablePath(KnownPath.EntryDirectory, $"EldenRingDryRun.{Guid.NewGuid():N}");
        base.AddServices(services)
            .AddFromSoftGames()
            .OverrideSettingsForTests<FromSoftSettings>(settings => settings with { DataRoot = dataRoot })
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<EldenRingGame>(
                provider,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.Steam])
            {
                // The stubbed hashes service only knows this id for Steam, and the locator reports it as the store identifier.
                LocatorIds = [LocatorId.From(StubbedSteamId)],
            })
            .AddHeadlessLaunch()
            .ReplaceDesktopNotifier();
        services.Replace(ServiceDescriptor.Singleton<IExecutableLocator, FakeBwrap>());
        services.Replace(ServiceDescriptor.Singleton<IUserIds>(new FixedIds(1000, 1001)));
        services.TryAddSingleton<LaunchSessionSlot>();
        return services;
    }

    private async Task<Loadout.ReadOnly> CreateLoadoutWith(params GamePath[] files)
    {
        var loadout = await CreateLoadout();
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, "Mods");
        foreach (var file in files) AddFile(tx, loadout.LoadoutId, group, file);
        await tx.Commit();
        return Refresh(loadout);
    }

    private async Task<(int ExitCode, string Output)> DryRun(Loadout.ReadOnly loadout)
    {
        LaunchArgs.TryParse([loadout.Name, "--dry-run", "--", "/steam/reaper", "SteamLaunch", $"AppId={StubbedSteamId}", "--", "/steam/proton", "waitforexitandrun", "/steam/ELDEN RING/Game/eldenring.exe"], out var args, out _).Should().BeTrue();

        var original = Console.Out;
        var originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetOut(captured);
        Console.SetError(captured);
        try
        {
            var exitCode = await LaunchRun.RunAsync(ServiceProvider, args!);
            return (exitCode, captured.ToString());
        }
        finally
        {
            Console.SetOut(original);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task AModdedLoadoutPrintsTheBwrapCommandWithMe3AfterTheSeparatorAndNothingOfSteamsChain()
    {
        var loadout = await CreateLoadoutWith(
            new GamePath(LocationId.Me3Host, "bin/me3"),
            new GamePath(LocationId.Me3Mods, "Weapons/parts/wp_a.dcx"));
        var me3 = GameInstallation.Locations[LocationId.Me3Host].Path / "bin/me3";

        var (exitCode, output) = await DryRun(loadout);

        exitCode.Should().Be(0, output);
        var command = output.Split('\n').Single(line => line.StartsWith("Command: ")).Trim();
        command.Should().StartWith("Command: /fake/bin/bwrap --unshare-user --uid 1000 --gid 1001");
        command.Should().Contain(" -- " + me3.GetFullPath() + " launch -p ");
        command.Should().NotContain("reaper").And.NotContain("proton");
        output.Should().Contain($"App id: {StubbedSteamId}");
        output.Should().Contain("Run tool: /fake/bin/bwrap with arguments --unshare-user");
        output.Should().Contain("Dry run: nothing was locked, applied or started.");
    }

    [Fact]
    public async Task AnUnmoddedLoadoutPrintsSteamsChainAroundTheGamesOwnExe()
    {
        var loadout = await CreateLoadout();

        var (exitCode, output) = await DryRun(loadout);

        exitCode.Should().Be(0, output);
        output.Should().Contain($"Command: /steam/reaper SteamLaunch AppId={StubbedSteamId} -- /steam/proton waitforexitandrun");
        output.Should().NotContain("bwrap");
    }

    [Fact]
    public async Task AModdedLoadoutWithoutMe3FailsThePlanWithAPlainMessage()
    {
        var loadout = await CreateLoadoutWith(new GamePath(LocationId.Me3Mods, "Weapons/parts/wp_a.dcx"));

        var (exitCode, _) = await DryRun(loadout);

        exitCode.Should().Be(LaunchSession.FailureExitCode);
    }
}
