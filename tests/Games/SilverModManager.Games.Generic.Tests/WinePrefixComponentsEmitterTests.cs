using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.Generic.Diagnostics;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using Xunit;

namespace SilverModManager.Games.Generic.Tests;

/// <summary>
/// The shared diagnostic for prefix components a game declares. The inspector and the prefix resolver are
/// fakes, so no test reads a real prefix.
/// </summary>
public class WinePrefixComponentsEmitterTests(ITestOutputHelper helper)
    : ALoadoutDiagnosticEmitterTest<WinePrefixComponentsEmitterTests, ComponentRequiringGame, WinePrefixComponentsEmitter>(helper)
{
    private static readonly DiagnosticId RequiredComponentsId = new("SilverModManager.Games.Generic", number: 7);

    private static readonly PrefixComponentId Vc = PrefixComponents.VisualCppRuntime.Id;
    private static readonly PrefixComponentId D3d = PrefixComponents.D3DCompiler47.Id;

    private readonly ILinuxCompatabilityDataProvider _provider = Substitute.For<ILinuxCompatabilityDataProvider>();

    private void NoWinetricksLog()
        => _provider.GetInstalledWinetricksComponents(Arg.Any<CancellationToken>()).Returns(new ValueTask<ImmutableHashSet<string>>([]));

    private FakePrefixInspector Inspector => ServiceProvider.GetRequiredService<FakePrefixInspector>();
    private FakePrefixResolver Resolver => ServiceProvider.GetRequiredService<FakePrefixResolver>();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGenericGameSupport()
            .AddGame<ComponentRequiringGame>()
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<ComponentRequiringGame>(
                provider,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.Steam]
            )
            {
                LocatorIds = [LocatorId.From("3976631895")],
                LinuxCompatabilityDataProvider = _provider,
            });
    }

    private string Details(Diagnostic diagnostic) => diagnostic.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>());

    [Fact]
    public async Task MissingComponentsAreNamedAndOfferTheSetUpAction()
    {
        NoWinetricksLog();
        var loadout = await CreateLoadout();
        Game.Required = [Vc, D3d];
        Inspector.State = id => id == Vc ? PrefixComponentState.Missing : PrefixComponentState.Installed;

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Id.Should().Be(RequiredComponentsId);
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Critical);
        diagnostic.Actions.Should().Equal(DiagnosticActions.SetUpPrefix([Vc.Value]));
        var details = Details(diagnostic);
        details.Should().Contain("vcrun2022");
        details.Should().NotContain("d3dcompiler_47", "an installed component is not part of the problem");
    }

    [Fact]
    public async Task TheDiagnosticKeepsTheWinetricksInstructionsAsFallbackText()
    {
        NoWinetricksLog();
        var loadout = await CreateLoadout();
        Game.Required = [Vc];
        Inspector.State = _ => PrefixComponentState.Missing;

        var diagnostic = await GetSingleDiagnostic(loadout);

        Details(diagnostic).Should().Contain("protontricks").And.Contain("vcrun2022");
    }

    [Fact]
    public async Task ComponentsThatCouldNotBeVerifiedAreListedAsSuch()
    {
        NoWinetricksLog();
        var loadout = await CreateLoadout();
        Game.Required = [Vc, D3d];
        Inspector.State = id => id == Vc ? PrefixComponentState.Missing : PrefixComponentState.Unknown;

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Actions.Should().Equal(new[] { DiagnosticActions.SetUpPrefix([Vc.Value]) }, "an unverified component is not installed on a guess");
        var details = Details(diagnostic);
        details.Should().Contain("could not verify");
        details.Should().Contain("d3dcompiler_47");
    }

    [Fact]
    public async Task OnlyUnverifiedComponentsReportNothing()
    {
        NoWinetricksLog();
        var loadout = await CreateLoadout();
        Game.Required = [Vc];
        Inspector.State = _ => PrefixComponentState.Unknown;

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task NothingIsReportedWhenEveryComponentIsInstalled()
    {
        NoWinetricksLog();
        var loadout = await CreateLoadout();
        Game.Required = [Vc, D3d];

        await ShouldHaveNoDiagnostics(loadout);
        Inspector.Calls.Should().ContainSingle().Which.Components.Should().Equal(Vc, D3d);
    }

    [Theory]
    [InlineData(CompatibilityLayerKind.Heroic)]
    [InlineData(CompatibilityLayerKind.HeroicFlatpak)]
    [InlineData(CompatibilityLayerKind.PlainWine)]
    public async Task AnyPrefixKindIsInspected(CompatibilityLayerKind kind)
    {
        NoWinetricksLog();
        var loadout = await CreateLoadout();
        Game.Required = [D3d];
        var prefix = new ResolvedPrefix(Resolver.DefaultPrefix.Path, kind);
        Resolver.Resolution = _ => PrefixResolution.Resolved(prefix);
        Inspector.State = _ => PrefixComponentState.Missing;

        var diagnostic = await GetSingleDiagnostic(loadout);

        Inspector.Calls.Should().ContainSingle().Which.Prefix.Should().Be(prefix);
        diagnostic.Actions.Should().Equal(DiagnosticActions.SetUpPrefix([D3d.Value]));
        Details(diagnostic).Should().Contain("winetricks").And.NotContain("protontricks", "only Steam has protontricks");
    }

    [Fact]
    public async Task AGameThatRequiresNothingNeverTouchesThePrefix()
    {
        NoWinetricksLog();
        var loadout = await CreateLoadout();
        Game.Required = [];

        await ShouldHaveNoDiagnostics(loadout);
        Inspector.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NothingIsReportedWithoutAPrefix(bool native)
    {
        NoWinetricksLog();
        var loadout = await CreateLoadout();
        Game.Required = [Vc];
        Resolver.Resolution = _ => native ? PrefixResolution.Native("native build") : PrefixResolution.Failed("no prefix");

        await ShouldHaveNoDiagnostics(loadout);
        Inspector.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AWinetricksLogEntryIsOnlyAHintNextToAMissingComponent()
    {
        var loadout = await CreateLoadout();
        Game.Required = [Vc];
        Inspector.State = _ => PrefixComponentState.Missing;
        _provider.GetInstalledWinetricksComponents(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ImmutableHashSet<string>>(["vcrun2022"]));

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Actions.Should().Equal(new[] { DiagnosticActions.SetUpPrefix([Vc.Value]) }, "winetricks.log does not prove the files are there");
        Details(diagnostic).Should().Contain("winetricks.log");
    }

    [Fact]
    public async Task APrefixThatCannotBeReadDoesNotFailTheEmitter()
    {
        NoWinetricksLog();
        var loadout = await CreateLoadout();
        Game.Required = [Vc];
        Inspector.Failure = new IOException("permission denied");

        await ShouldHaveNoDiagnostics(loadout);
    }
}

/// <summary>A stubbed game whose required prefix components each test sets.</summary>
public class ComponentRequiringGame(IServiceProvider provider) : StubbedGame(provider), IWinePrefixRequirements
{
    public IReadOnlyList<PrefixComponentId> Required { get; set; } = [];

    public IReadOnlyList<PrefixComponentId> RequiredPrefixComponents(Loadout.ReadOnly loadout) => Required;
}
