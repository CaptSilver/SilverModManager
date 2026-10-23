using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.Generic.Diagnostics;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using NSubstitute;
using Xunit;

namespace SilverModManager.Games.Generic.Tests;

/// <summary>
/// The shared Wine DLL override diagnostic for games that declare their overrides. The prefix is a
/// substitute, so no real prefix is read.
/// </summary>
public class WineDllOverrideEmitterTests(ITestOutputHelper helper)
    : ALoadoutDiagnosticEmitterTest<WineDllOverrideEmitterTests, OverrideRequiringGame, WineDllOverrideEmitter>(helper)
{
    private static readonly DiagnosticId RequiredOverridesId = new("SilverModManager.Games.Generic", number: 6);

    private static readonly WineDllOverride Winhttp = new("winhttp", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn]);

    private readonly ILinuxCompatabilityDataProvider _provider = Substitute.For<ILinuxCompatabilityDataProvider>();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGenericGameSupport()
            .AddGame<OverrideRequiringGame>()
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<OverrideRequiringGame>(
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

    private void Require(params WineDllOverride[] overrides) => Game.Required = overrides;

    private void SetPrefixOverrides(params WineDllOverride[] overrides)
    {
        _provider.GetWineDllOverrides(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ImmutableArray<WineDllOverride>>([..overrides]));
    }

    [Fact]
    public async Task AMissingOverrideIsReportedWithTheSteamFix()
    {
        var loadout = await CreateLoadout();
        Require(Winhttp);
        SetPrefixOverrides();

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Id.Should().Be(RequiredOverridesId);
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Critical);
        diagnostic.Actions.Should().Equal(DiagnosticActions.UseLoadoutFromSteam());
        diagnostic.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>()).Should().Contain("winhttp");
    }

    [Fact]
    public async Task NothingIsReportedOnceThePrefixHasTheOverride()
    {
        var loadout = await CreateLoadout();
        Require(Winhttp);
        SetPrefixOverrides(Winhttp);

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task AGameThatRequiresNothingNeverReadsThePrefix()
    {
        var loadout = await CreateLoadout();
        Require();

        await ShouldHaveNoDiagnostics(loadout);
        await _provider.DidNotReceive().GetWineDllOverrides(Arg.Any<CancellationToken>());
    }
}

public class WineDllOverrideEmitterWithoutAPrefixTests(ITestOutputHelper helper)
    : ALoadoutDiagnosticEmitterTest<WineDllOverrideEmitterWithoutAPrefixTests, OverrideRequiringGame, WineDllOverrideEmitter>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGenericGameSupport()
            .AddGame<OverrideRequiringGame>()
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<OverrideRequiringGame>(
                provider,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.Steam]
            )
            {
                LocatorIds = [LocatorId.From("3976631895")],
            });
    }

    [Fact]
    public async Task NothingIsReportedWhileThePrefixIsNotReady()
    {
        var loadout = await CreateLoadout();
        Game.Required = [new WineDllOverride("winhttp", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn])];

        await ShouldHaveNoDiagnostics(loadout);
    }
}

/// <summary>A stubbed game whose required overrides each test sets.</summary>
public class OverrideRequiringGame(IServiceProvider provider) : StubbedGame(provider), IWineDllOverrideRequirements
{
    public IReadOnlyList<WineDllOverride> Required { get; set; } = [];

    public IReadOnlyList<WineDllOverride> RequiredWineDllOverrides(Loadout.ReadOnly loadout) => Required;
}
