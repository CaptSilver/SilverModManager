using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.Backend;
using SilverModManager.CrossPlatform;
using SilverModManager.Games.Generic;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.RedEngine.Cyberpunk2077.Emitters;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using NSubstitute;
using Xunit;

namespace SilverModManager.Games.RedEngine.Tests;

/// <summary>
/// The Wine emitter reports what the game declares and nothing of its own. The prefix is a substitute,
/// so no test reads a real Wine prefix or Steam directory.
/// </summary>
public class Cyberpunk2077WineEmitterTests(ITestOutputHelper helper) : AIsolatedGameTest<Cyberpunk2077WineEmitterTests, Cyberpunk2077Game>(helper)
{
    private readonly ILinuxCompatabilityDataProvider _provider = Substitute.For<ILinuxCompatabilityDataProvider>();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddOSInterop()
            .AddRuntimeDependencies()
            .RemoveRuntimeDependencyChecker()
            .AddGenericGameSupport()
            .AddFakeToolRunner()
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<Cyberpunk2077Game>(
                provider,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<TemporaryFileManager>()
            )
            {
                LinuxCompatabilityDataProvider = _provider,
            })
            .AddRedEngineGames();
    }

    [Fact]
    public async Task TheWineEmitterReportsOnlyWhatTheGameDeclares()
    {
        // Every winetricks package is present, so the only thing that can be reported is a DLL override.
        _provider.GetInstalledWinetricksComponents(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ImmutableHashSet<string>>(["d3dcompiler_47", "vcrun2022"]));
        var loadout = await CreateLoadout();
        var emitter = Game.DiagnosticEmitters.OfType<WinePrefixRequirementsEmitter>().Single();

        SetPrefixOverrides(((IWineDllOverrideRequirements)Game).RequiredWineDllOverrides(loadout).ToArray());
        (await Overrides(emitter, loadout)).Should().BeEmpty("the prefix already has exactly what the game declares");

        SetPrefixOverrides(new WineDllOverride("winmm", [WineDllOverrideType.BuiltIn]), new WineDllOverride("version", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn]));
        var reported = await Overrides(emitter, loadout);

        reported.Should().ContainSingle().Which.MessageData.Instructions.Should().Contain("winmm=n,b;version=n,b");
        reported.Single().Actions.Should().BeEmpty("a game from an unknown store has no Steam launch options to write");
    }

    [Fact]
    public void CyberpunkDeclaresItsSaveFolderForTheBrokenLinkCheckWithoutMappingIt()
    {
        ((IGameUserDataFolders)Game).UserDataFolders.Should().Equal(
            new UserDataFolder(KnownPath.HomeDirectory, "Saved Games/CD Projekt Red/Cyberpunk 2077"));
        Game.DiagnosticEmitters.OfType<SilverModManager.Games.Generic.Diagnostics.UserDataLinkEmitter>().Should().ContainSingle();
    }

    [Fact]
    public void CyberpunkDeclaresTheArgumentThatSkipsREDprelauncher()
    {
        ((IGameLauncherSkip)Game).LauncherSkipArguments.Should().Equal("--launcher-skip");
        GameLauncherSkip.For(Game).Should().Equal("--launcher-skip");
    }

    private void SetPrefixOverrides(params WineDllOverride[] overrides)
    {
        _provider.GetWineDllOverrides(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ImmutableArray<WineDllOverride>>([..overrides]));
    }

    private static async Task<List<Diagnostic<Diagnostics.RequiredWineDllOverridesMessageData>>> Overrides(WinePrefixRequirementsEmitter emitter, Loadout.ReadOnly loadout)
    {
        var diagnostics = await emitter.Diagnose(loadout, CancellationToken.None).ToListAsync();
        return diagnostics.OfType<Diagnostic<Diagnostics.RequiredWineDllOverridesMessageData>>().ToList();
    }
}

/// <summary>
/// On a Steam install the app's own launch line sets the overrides, so the diagnostic offers to write it.
/// </summary>
public class Cyberpunk2077WineEmitterSteamTests(ITestOutputHelper helper) : AIsolatedGameTest<Cyberpunk2077WineEmitterSteamTests, Cyberpunk2077Game>(helper)
{
    private readonly ILinuxCompatabilityDataProvider _provider = Substitute.For<ILinuxCompatabilityDataProvider>();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddOSInterop()
            .AddRuntimeDependencies()
            .RemoveRuntimeDependencyChecker()
            .AddGenericGameSupport()
            .AddFakeToolRunner()
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<Cyberpunk2077Game>(
                provider,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.Steam]
            )
            {
                LinuxCompatabilityDataProvider = _provider,
                // A numeric id the stub hasher knows; Steam store ids are numeric app ids.
                LocatorIds = [LocatorId.From("3976631895")],
            })
            .AddRedEngineGames();
    }

    [Fact]
    public async Task TheOverridesDiagnosticOffersTheSteamLaunchOptionFix()
    {
        _provider.GetInstalledWinetricksComponents(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ImmutableHashSet<string>>(["d3dcompiler_47", "vcrun2022"]));
        _provider.GetWineDllOverrides(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ImmutableArray<WineDllOverride>>(ImmutableArray<WineDllOverride>.Empty));
        var loadout = await CreateLoadout();
        var emitter = Game.DiagnosticEmitters.OfType<WinePrefixRequirementsEmitter>().Single();

        var diagnostics = await emitter.Diagnose(loadout, CancellationToken.None).ToListAsync();

        diagnostics.OfType<Diagnostic<Diagnostics.RequiredWineDllOverridesMessageData>>().Should().ContainSingle()
            .Which.Actions.Should().Equal(DiagnosticActions.UseLoadoutFromSteam());
    }

    [Fact]
    public async Task TheWinetricksDiagnosticHasNoFixAction()
    {
        _provider.GetInstalledWinetricksComponents(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ImmutableHashSet<string>>([]));
        _provider.GetWineDllOverrides(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ImmutableArray<WineDllOverride>>(ImmutableArray<WineDllOverride>.Empty));
        var loadout = await CreateLoadout();
        var emitter = Game.DiagnosticEmitters.OfType<WinePrefixRequirementsEmitter>().Single();

        var diagnostics = await emitter.Diagnose(loadout, CancellationToken.None).ToListAsync();

        diagnostics.OfType<Diagnostic<Diagnostics.RequiredWinetricksPackagesMessageData>>().Should().ContainSingle()
            .Which.Actions.Should().BeEmpty("the launch line cannot install winetricks packages");
    }
}
