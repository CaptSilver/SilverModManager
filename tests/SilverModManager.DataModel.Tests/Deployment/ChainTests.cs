using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.DataModel.Tests.Deployment;

public class ChainTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<ChainTests>(helper)
{
    private static readonly GamePath GameRoot = new(LocationId.Game, RelativePath.Empty);

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services).AddRecordingDeploymentBackend();
    }

    private DeploymentChoice Choose(GamePath root, params IDeploymentBackend[] backends)
        => new DeploymentBackendChain(backends).Choose(GameInstallation, root);

    private IDeploymentBackend Copy => ServiceProvider.GetRequiredService<CopyDeploymentBackend>();

    [Fact]
    public void OrderIsReflinkHardlinkSymlinkCopy()
    {
        DeploymentBackendChain.Order.Should().Equal(
            DeploymentBackendId.Reflink, DeploymentBackendId.Hardlink, DeploymentBackendId.Symlink, DeploymentBackendId.Copy);
    }

    [Fact]
    public void ChoosesTheFirstAcceptingBackendInOrder()
    {
        var reflink = new ProbedBackend(DeploymentBackendId.Reflink, DeploymentPreflight.Refuse("no reflink"));
        var hardlink = new ProbedBackend(DeploymentBackendId.Hardlink, DeploymentPreflight.Accept("same device"));
        var symlink = new ProbedBackend(DeploymentBackendId.Symlink, DeploymentPreflight.Accept("always"));

        // Registration order must not matter, only Order does.
        var choice = Choose(GameRoot, Copy, symlink, hardlink, reflink);

        choice.Backend.Should().Be(DeploymentBackendId.Hardlink);
        symlink.PreflightCalls.Should().BeEmpty("the walk stops at the first accepting backend");
    }

    [Fact]
    public void SkipsUnregisteredIds()
    {
        var symlink = new ProbedBackend(DeploymentBackendId.Symlink, DeploymentPreflight.Accept("always"));

        var choice = Choose(GameRoot, Copy, symlink);

        choice.Backend.Should().Be(DeploymentBackendId.Symlink);
        choice.Reason.Should().NotContain("reflink").And.NotContain("hardlink");
    }

    [Fact]
    public void ReasonListsEveryRefusalBeforeTheChoice()
    {
        var reflink = new ProbedBackend(DeploymentBackendId.Reflink, DeploymentPreflight.Refuse("ioctl(FICLONE) failed: EOPNOTSUPP (filesystem has no reflink)"));
        var hardlink = new ProbedBackend(DeploymentBackendId.Hardlink, DeploymentPreflight.Accept("accepted"));

        var choice = Choose(GameRoot, Copy, hardlink, reflink);

        choice.Reason.Should().Be("reflink: ioctl(FICLONE) failed: EOPNOTSUPP (filesystem has no reflink); hardlink: accepted");
    }

    [Fact]
    public void CopyIsChosenWhenEveryLinkBackendRefuses()
    {
        var reflink = new ProbedBackend(DeploymentBackendId.Reflink, DeploymentPreflight.Refuse("a"));
        var hardlink = new ProbedBackend(DeploymentBackendId.Hardlink, DeploymentPreflight.Refuse("b"));
        var symlink = new ProbedBackend(DeploymentBackendId.Symlink, DeploymentPreflight.Refuse("c"));

        var choice = Choose(GameRoot, Copy, reflink, hardlink, symlink);

        choice.Backend.Should().Be(DeploymentBackendId.Copy);
        choice.Reason.Should().StartWith("reflink: a; hardlink: b; symlink: c; copy: ");
    }

    [Fact]
    public void ARegisteredBackendOutsideTheOrderIsNeverChosen()
    {
        var recording = ServiceProvider.GetRequiredService<RecordingDeploymentBackend>();

        var choice = Choose(GameRoot, recording, Copy);

        choice.Backend.Should().Be(DeploymentBackendId.Copy);
        recording.Calls.Should().NotContain(c => c.Phase == "Preflight");
    }

    [Fact]
    public void AFilteredLocationIsCopyWithoutAProbe()
    {
        var reflink = new ProbedBackend(DeploymentBackendId.Reflink, DeploymentPreflight.Accept("anything"));

        var choice = Choose(new GamePath(LocationId.Saves, RelativePath.Empty), Copy, reflink);

        choice.Backend.Should().Be(DeploymentBackendId.Copy);
        choice.Reason.Should().Be("location is never written");
        reflink.PreflightCalls.Should().BeEmpty();
    }

    [Fact]
    public void ThrowsWhenNothingAcceptsNotEvenCopy()
    {
        var refusingCopy = new ProbedBackend(DeploymentBackendId.Copy, DeploymentPreflight.Refuse("read-only"));

        var act = () => Choose(GameRoot, refusingCopy);

        act.Should().Throw<InvalidOperationException>().WithMessage("*copy: read-only*");
    }

    [Fact]
    public void TheCopyBackendAcceptsEveryRootAndNeedsNoExtraFilesystems()
    {
        Copy.Preflight(GameInstallation, GameRoot).Accepted.Should().BeTrue();
        Copy.RequiredFilesystems(GameInstallation, GameRoot).Should().BeEmpty();
    }

    /// <summary>
    /// A backend that only answers <see cref="Preflight"/>; every file-touching phase throws, so a test that
    /// accidentally runs one fails loudly.
    /// </summary>
    private sealed class ProbedBackend(DeploymentBackendId id, DeploymentPreflight answer) : IDeploymentBackend
    {
        public List<GamePath> PreflightCalls { get; } = [];

        public DeploymentBackendId Id => id;
        public bool UsesLooseStaging => false;

        public DeploymentPreflight Preflight(GameInstallation installation, GamePath root)
        {
            PreflightCalls.Add(root);
            return answer;
        }

        public IReadOnlyList<AbsolutePath> RequiredFilesystems(GameInstallation installation, GamePath root) => [];

        public Task Backup(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();
        public void Ingest(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();
        public Task AdaptLoadout(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();
        public IReadOnlyCollection<GamePath> Delete(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();
        public Task Extract(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();
        public Task WriteIntrinsics(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();
        public void AddReifiedDeletes(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();
        public void CleanDirectories(DeploymentContext context, IReadOnlyCollection<GamePath> directoriesWithDeletions, Entities<DiskStateEntry.ReadOnly> newDiskState) => throw new NotSupportedException();
    }
}
