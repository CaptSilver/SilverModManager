using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FileHashes.Emitters;
using NexusMods.Games.FromSoft.Build;
using NexusMods.Games.FromSoft.Diagnostics;
using NexusMods.Games.FromSoft.Doctor;
using NexusMods.Games.FromSoft.Heal;
using NexusMods.Games.FromSoft.Merge;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Installers;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Games.Generic.Diagnostics;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Tests;

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
