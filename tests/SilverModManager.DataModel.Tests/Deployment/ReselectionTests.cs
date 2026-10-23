using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.DataModel.Synchronizer;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Native;

namespace SilverModManager.DataModel.Tests.Deployment;

/// <summary>
/// What an apply does with rows that were recorded before the selection chain could pick a link backend.
/// </summary>
public class ReselectionTests(ITestOutputHelper helper) : AppliedGameTest<ReselectionTests>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        // Registered first: the data model only adds the chain when nothing else is there.
        services.AddSingleton<IDeploymentBackendSelector>(sp => new CopyUntilToldOtherwise(sp));
        return base.AddServices(services);
    }

    private CopyUntilToldOtherwise Selector => (CopyUntilToldOtherwise)ServiceProvider.GetRequiredService<IDeploymentBackendSelector>();

    private FakeGameLaunchSession LaunchSession => ServiceProvider.GetRequiredService<FakeGameLaunchSession>();

    private async Task<(Sdk.Loadouts.Loadout.ReadOnly Loadout, string Applied)> AppliedWithCopy()
    {
        var loadout = await NewLoadoutWithMod();
        await SynchronizerService.Synchronize(loadout.LoadoutId);
        Plan.Roots.Should().OnlyContain(r => r.Backend == DeploymentBackendId.Copy && !r.Chosen);
        return (loadout, await DescribeGameFolder());
    }

    private (ulong Inode, long ChangeSeconds, long ChangeNanoseconds) Identity(GamePath path)
    {
        LibcStat.TryLStat(OnDisk(path).ToString(), out var stat).Should().Be(0);
        return (stat.Inode, stat.ChangeSeconds, stat.ChangeNanoseconds);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AnUnchosenCopyRowMovesToTheChainsChoiceAtTheNextApply()
    {
        var (loadout, applied) = await AppliedWithCopy();
        Selector.UseChain = true;
        var chosen = Selector.Choose(GameInstallation, GameRoot).Backend;
        chosen.Should().NotBe(DeploymentBackendId.Copy, "the chain finds a link backend where the game and the store share a filesystem");

        await SynchronizerService.Synchronize(loadout.LoadoutId);

        var game = Plan.Roots.Single(r => r.Root == GameRoot);
        game.Backend.Should().Be(chosen);
        game.Chosen.Should().BeFalse("the chain moved it, nobody picked it");
        (await DescribeGameFolder()).Should().Be(applied, "the deployed tree is the same whatever deploys it");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AChosenCopyRowStaysCopyAndNothingIsReset()
    {
        var (loadout, applied) = await AppliedWithCopy();
        using (var tx = Connection.BeginTransaction())
        {
            // Every root: moving any one of them resets the whole install, so one unchosen row would reset the rest.
            foreach (var row in Plan.Roots)
                Plans.Write(tx, Connection.Db, MetadataId, row.Root, DeploymentBackendId.Copy, chosen: true);
            await tx.Commit();
        }
        var before = Identity(ModFile);
        Selector.UseChain = true;

        await SynchronizerService.Synchronize(loadout.LoadoutId);

        var game = Plan.Roots.Single(r => r.Root == GameRoot);
        game.Backend.Should().Be(DeploymentBackendId.Copy);
        game.Chosen.Should().BeTrue();
        Identity(ModFile).Should().Be(before, "a reset would have deleted the file and extracted it again");
        (await DescribeGameFolder()).Should().Be(applied);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AnUncoveredBuildKeepsItsBackendAndTheApplyStillRuns()
    {
        var (loadout, applied) = await AppliedWithCopy();
        var before = Identity(ModFile);
        Selector.UseChain = true;
        ((StubbedFileHasherService)ServiceProvider.GetRequiredService<IFileHashesService>()).CoversEveryBuild = false;

        var act = () => SynchronizerService.Synchronize(loadout.LoadoutId);

        await act.Should().NotThrowAsync("the move is skipped, the apply goes ahead with the backend the root has");
        Plan.Roots.Should().OnlyContain(r => r.Backend == DeploymentBackendId.Copy, "moving would have reset the folder by a vanilla list that does not cover the build");
        Identity(ModFile).Should().Be(before, "a reset would have deleted the file and extracted it again");
        (await DescribeGameFolder()).Should().Be(applied);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task ALaunchLeavesAnUnchosenCopyRowAlone()
    {
        var (loadout, applied) = await AppliedWithCopy();
        var before = Identity(ModFile);
        Selector.UseChain = true;

        await ((SynchronizerService)SynchronizerService).SynchronizeFromLaunchSession(loadout.LoadoutId);

        Plan.Roots.Should().OnlyContain(r => r.Backend == DeploymentBackendId.Copy);
        Identity(ModFile).Should().Be(before);
        (await DescribeGameFolder()).Should().Be(applied);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task ARunningGameRefusesTheMoveBeforeAnythingIsReset()
    {
        var (loadout, applied) = await AppliedWithCopy();
        var before = Identity(ModFile);
        Selector.UseChain = true;
        LaunchSession.Running = true;

        var act = () => SynchronizerService.Synchronize(loadout.LoadoutId);

        await act.Should().ThrowAsync<ExecutableInUseException>();
        Plan.Roots.Should().OnlyContain(r => r.Backend == DeploymentBackendId.Copy);
        Identity(ModFile).Should().Be(before);
        (await DescribeGameFolder()).Should().Be(applied);
    }

    /// <summary>
    /// Picks copy for every root, as the build that wrote the existing rows did, until the test switches the real chain on.
    /// </summary>
    private sealed class CopyUntilToldOtherwise(IServiceProvider services) : IDeploymentBackendSelector
    {
        public bool UseChain { get; set; }

        public DeploymentChoice Choose(GameInstallation installation, GamePath root) => UseChain
            ? new DeploymentBackendChain(services.GetServices<IDeploymentBackend>()).Choose(installation, root)
            : new DeploymentChoice(DeploymentBackendId.Copy, "copy only");
    }
}
