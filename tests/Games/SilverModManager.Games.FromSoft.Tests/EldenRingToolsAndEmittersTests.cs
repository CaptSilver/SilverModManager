using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FileHashes.Emitters;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Diagnostics;
using SilverModManager.Games.FromSoft.Doctor;
using SilverModManager.Games.FromSoft.Heal;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Installers;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.Generic.Diagnostics;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Tests;

public class EldenRingToolsAndEmittersTests(ITestOutputHelper helper) : AEldenRingTest<EldenRingToolsAndEmittersTests>(helper)
{
    private sealed class FakeEmitter : IFromSoftDiagnosticEmitter
    {
        [Obsolete("Replaced by the overload that takes the sync tree")]
        public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services).AddSingleton<IFromSoftDiagnosticEmitter, FakeEmitter>();

    private ITool[] ToolsOfTheGame => ServiceProvider.GetServices<ITool>().Where(t => t.GameIds.Contains(EldenRingGame.GameId)).ToArray();

    [Fact]
    public void TheOnlyToolIsTheOverlayLauncher()
    {
        ToolsOfTheGame.Should().ContainSingle().Subject.Should().BeOfType<RunEldenRingGame>();
    }

    [Fact]
    public async Task NoRunToolForAnUnmoddedLoadoutResolvesAnythingButThePrimaryFile()
    {
        var loadout = await CreateLoadout();
        var primary = GameInstallation.Locations.ToAbsolutePath(Game.GetPrimaryFile(GameInstallation));

        foreach (var tool in ToolsOfTheGame.OfType<IRunGameTool>())
        {
            var spec = await tool.GetLaunchSpec(loadout, CancellationToken.None);
            spec.Exe.Should().Be(primary, $"{tool.Name} with nothing installed starts the game as Steam does");
            spec.ExtraArgs.Should().BeEmpty();
        }
    }

    [Fact]
    public void TheFixedEmittersAndTheOnesRegisteredInDependencyInjectionAreTheGamesEmitters()
    {
        Game.DiagnosticEmitters.Select(e => e.GetType()).Should().Equal(
            typeof(NoWayToSourceFilesOnDisk),
            typeof(UndeployableLoadoutDueToMissingGameFiles),
            typeof(ProtonPrefixEmitter),
            typeof(PackageCollisionEmitter),
            typeof(MergeDiagnosticsEmitter),
            typeof(HealEmitter),
            typeof(Me3HostMissingEmitter),
            typeof(UpperdirAuditEmitter),
            typeof(GameFolderArtifactsEmitter),
            typeof(SpawnerEmitter),
            typeof(OverlaySupportEmitter),
            typeof(SaveWrittenByModdedSessionEmitter),
            typeof(BuildDriftEmitter),
            typeof(FakeEmitter));
    }

    [Fact]
    public void TheInstallersAreTriedInTheOrderHostThenSeamlessCoopThenPackageThenNative()
    {
        Game.LibraryItemInstallers.Select(i => i.GetType()).Should().Equal(
            typeof(Me3HostInstaller),
            typeof(SeamlessCoopInstaller),
            typeof(Me3PackageInstaller),
            typeof(Me3NativeInstaller));
    }
}
