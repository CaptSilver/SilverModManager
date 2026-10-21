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
using NexusMods.Games.FromSoft.Diagnostics;
using NexusMods.Games.FromSoft.Doctor;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Installers;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Games.Generic.Diagnostics;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Tests;

public class EldenRingToolsAndEmittersTests(ITestOutputHelper helper) : AEldenRingTest<EldenRingToolsAndEmittersTests>(helper)
{
    private sealed class FakeEmitter : IEldenRingDiagnosticEmitter
    {
        [Obsolete("Replaced by the overload that takes the sync tree")]
        public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services).AddSingleton<IEldenRingDiagnosticEmitter, FakeEmitter>();

    private ITool[] ToolsOfTheGame => ServiceProvider.GetServices<ITool>().Where(t => t.GameIds.Contains(EldenRingGame.GameId)).ToArray();

    [Fact]
    public async Task TheOnlyToolIsALauncherThatRefusesBecauseTheOverlayLauncherIsNotThereYet()
    {
        var loadout = await CreateLoadout();

        var tool = ToolsOfTheGame.Should().ContainSingle().Subject.Should().BeOfType<RunEldenRingGame>().Subject;

        var launch = async () => await tool.GetLaunchSpec(loadout, CancellationToken.None);
        (await launch.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("Elden Ring is launched through the overlay launcher, which is not available yet");
    }

    [Fact]
    public async Task NoRunToolCouldStartAFileInsideTheGameLocation()
    {
        var loadout = await CreateLoadout();
        var gameFolder = GameInstallation.Locations[LocationId.Game].Path;

        foreach (var tool in ToolsOfTheGame.OfType<IRunGameTool>())
        {
            LaunchSpec spec;
            try { spec = await tool.GetLaunchSpec(loadout, CancellationToken.None); }
            catch (InvalidOperationException) { continue; }

            spec.Exe.InFolder(gameFolder).Should().BeFalse($"{tool.Name} would start a file in the game folder");
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
            typeof(FakeEmitter));
    }

    [Fact]
    public void TheInstallersAreTriedInTheOrderSeamlessCoopThenPackageThenNative()
    {
        Game.LibraryItemInstallers.Select(i => i.GetType()).Should().Equal(
            typeof(SeamlessCoopInstaller),
            typeof(Me3PackageInstaller),
            typeof(Me3NativeInstaller));
    }
}
