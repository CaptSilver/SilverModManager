using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Games;
using NexusMods.Backend;
using NexusMods.CrossPlatform;
using NexusMods.Games.Generic;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.RedEngine.Cyberpunk2077.Emitters;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;
using NSubstitute;
using Xunit;

namespace NexusMods.Games.RedEngine.Tests;

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
