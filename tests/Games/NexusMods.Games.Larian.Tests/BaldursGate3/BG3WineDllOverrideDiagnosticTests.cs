using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.App.UI.DiagnosticSystem;
using NexusMods.Games.Larian.BaldursGate3;
using NexusMods.Games.Larian.BaldursGate3.Emitters;
using NexusMods.Games.Larian.BaldursGate3.Installers;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;
using NSubstitute;
using Xunit;

namespace NexusMods.Games.Larian.Tests.BaldursGate3;

/// <summary>
/// The script extender's DLL override diagnostic follows the prefix: it is reported while DWrite is not
/// overridden and clears once it is. The game is located through GOG because Wine needs the override
/// whichever store launched it. The prefix is a substitute, so no real prefix is read.
/// </summary>
public class BG3WineDllOverrideDiagnosticTests(ITestOutputHelper outputHelper) : ALibraryArchiveInstallerTests<BG3WineDllOverrideDiagnosticTests, Larian.BaldursGate3.BaldursGate3>(outputHelper)
{
    private static readonly DiagnosticId ScriptExtenderOverrideId = new("NexusMods.Games.Larian.BaldursGate3", number: 5);

    private readonly ILinuxCompatabilityDataProvider _provider = Substitute.For<ILinuxCompatabilityDataProvider>();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddBaldursGate3()
            .AddDiagnosticWriter()
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<Larian.BaldursGate3.BaldursGate3>(
                provider,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.GOG]
            )
            {
                // GOG locator ids are numeric build ids, which the hashes queries cast; the stub hasher knows this one.
                LocatorIds = [LocatorId.From("3976631895")],
                LinuxCompatabilityDataProvider = _provider,
            });
    }

    [Fact]
    public async Task TheScriptExtenderOverrideDiagnosticClearsOnceThePrefixOverridesDWrite()
    {
        var loadout = await CreateLoadout();
        var archive = await AddFromPaths("DWrite.dll", "ScriptExtenderSettings.json");
        await Install(typeof(BG3SEInstaller), loadout, archive);
        Refresh(ref loadout);

        SetPrefixOverrides();
        var reported = await ScriptExtenderOverrideDiagnostics(loadout);
        reported.Should().ContainSingle()
            .Which.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>())
            .Should().Contain("WINEDLLOVERRIDES=\"DWrite=n,b\"");

        SetPrefixOverrides(new WineDllOverride("DWrite", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn]));
        (await ScriptExtenderOverrideDiagnostics(loadout)).Should().BeEmpty("the prefix already overrides DWrite");
    }

    [Fact]
    public async Task ALoadoutWithoutTheScriptExtenderNeverReadsThePrefix()
    {
        var loadout = await CreateLoadout();

        (await ScriptExtenderOverrideDiagnostics(loadout)).Should().BeEmpty();
        await _provider.DidNotReceive().GetWineDllOverrides(Arg.Any<CancellationToken>());
    }

    private void SetPrefixOverrides(params WineDllOverride[] overrides)
    {
        _provider.GetWineDllOverrides(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ImmutableArray<WineDllOverride>>([..overrides]));
    }

    private async Task<List<Diagnostic>> ScriptExtenderOverrideDiagnostics(Loadout.ReadOnly loadout)
    {
        var emitter = Game.DiagnosticEmitters.OfType<DependencyDiagnosticEmitter>().Single();
        var diagnostics = await emitter.Diagnose(loadout, CancellationToken.None).ToListAsync();
        return diagnostics.Where(d => d.Id == ScriptExtenderOverrideId).ToList();
    }
}
