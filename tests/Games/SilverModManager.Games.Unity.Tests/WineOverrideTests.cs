using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.Generic;
using SilverModManager.Games.Generic.Diagnostics;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.Unity.Tests.Fixtures;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using NSubstitute;

namespace SilverModManager.Games.Unity.Tests;

/// <summary>
/// The proxy DLL's override reaches the Steam launch line and the shared Wine diagnostic through the game's
/// declaration alone. The prefix is a substitute, so no real prefix or Steam directory is read.
/// </summary>
public class WineOverrideTests(ITestOutputHelper outputHelper) : AUnityGameTests<WineOverrideTests, Unity.Repo.Repo>(outputHelper)
{
    private static readonly WineDllOverride Winhttp = new("winhttp", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn]);

    private readonly ILinuxCompatabilityDataProvider _provider = Substitute.For<ILinuxCompatabilityDataProvider>();

    protected override IServiceCollection AddGameLocator(IServiceCollection services)
    {
        return services
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<Unity.Repo.Repo>(
                provider,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.Steam]
            )
            {
                // Steam locator ids are numeric build ids, which the hashes queries cast; the stub hasher knows this one.
                LocatorIds = [LocatorId.From("3976631895")],
                LinuxCompatabilityDataProvider = _provider,
            });
    }

    private async Task<(Loadout.ReadOnly Loadout, EntityId Pack)> LoadoutWithPack()
    {
        var loadout = await CreateLoadout();
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, "BepInEx-BepInExPack");
        AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, "winhttp.dll"));
        AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, "doorstop_config.ini"));
        var pack = (await tx.Commit())[group.Value];
        return (Refresh(loadout), pack);
    }

    private void SetPrefixOverrides(params WineDllOverride[] overrides)
    {
        _provider.GetWineDllOverrides(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ImmutableArray<WineDllOverride>>([..overrides]));
    }

    private async Task<Diagnostic[]> Diagnose(Loadout.ReadOnly loadout)
    {
        var emitter = Game.DiagnosticEmitters.OfType<WineDllOverrideEmitter>().Single();
        return await emitter.Diagnose(loadout, CancellationToken.None).ToArrayAsync();
    }

    [Fact]
    public async Task ThePackAsksForTheNativeProxyDll()
    {
        var (loadout, _) = await LoadoutWithPack();

        var overrides = WineDllOverrideRequirements.For(GameInstallation.Game, loadout);

        overrides.Should().ContainSingle().Which.DllName.Should().Be("winhttp");
        overrides.Single().OverrideTypes.Should().Equal(WineDllOverrideType.Native, WineDllOverrideType.BuiltIn);
    }

    [Fact]
    public async Task TheSteamLaunchLineSetsTheOverrideWhileThePackIsEnabled()
    {
        var (loadout, _) = await LoadoutWithPack();
        var overrides = WineDllOverrideRequirements.For(GameInstallation.Game, loadout);

        var line = SteamLaunchOptionLine.Compose("/shim/silvermod", loadout.Name, overrides, []);

        line.Should().Contain("WINEDLLOVERRIDES=\"winhttp=n,b\"");
    }

    [Fact]
    public async Task TheSteamLaunchLineHasNoOverrideWhileThePackIsDisabled()
    {
        var (loadout, pack) = await LoadoutWithPack();
        await DisableItem(pack);
        var refreshed = Refresh(loadout);
        var overrides = WineDllOverrideRequirements.For(GameInstallation.Game, refreshed);

        var line = SteamLaunchOptionLine.Compose("/shim/silvermod", refreshed.Name, overrides, []);

        line.Should().NotContain("WINEDLLOVERRIDES");
    }

    [Fact]
    public async Task AMissingOverrideInThePrefixIsReportedWithTheSteamFix()
    {
        var (loadout, _) = await LoadoutWithPack();
        SetPrefixOverrides();

        var diagnostics = await Diagnose(loadout);

        diagnostics.Should().ContainSingle().Which.Actions.Should().Equal(DiagnosticActions.UseLoadoutFromSteam());
    }

    [Fact]
    public async Task NothingIsReportedOnceThePrefixHasTheOverride()
    {
        var (loadout, _) = await LoadoutWithPack();
        SetPrefixOverrides(Winhttp);

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task NothingIsReportedWhenThePackIsNotInstalled()
    {
        var loadout = await CreateLoadout();
        SetPrefixOverrides();

        (await Diagnose(loadout)).Should().BeEmpty("without the pack the game requires nothing");
    }
}
