using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Deployment.Vfs.Tests;

public class MountSourceBuilderTests
{
    private static readonly GamePath Root = new(LocationId.Game, "Data");

    private static FileIdentity Id(ulong value) => FileIdentity.XxHash3(Hash.From(value));

    private static AbsolutePath StagedAt(FileIdentity identity) => FileSystem.Shared.FromUnsanitizedFullPath($"/staging/{identity}");

    private static IReadOnlyList<VfsFileSource> Build(MountManifestView manifest, params GamePath[] deleted) =>
        MountSourceBuilder.Build(manifest, deleted, StagedAt);

    [Fact]
    public void ARowBecomesAStagedSourceRelativeToTheRoot()
    {
        var row = new MountManifestRow(Root / "meshes/a.nif", Id(2), Size.FromLong(20), Id(1), false, null);

        var source = Build(new MountManifestView(Root, [row])).Single();

        source.Path.Should().Be(RelativePath.FromUnsanitizedInput("meshes/a.nif"));
        source.Kind.Should().Be(VfsSourceKind.Staged);
        source.RealPath.Should().Be(StagedAt(Id(2)));
        source.Size.Should().Be(Size.FromLong(20));
        source.Identity.Should().Be(Id(2));
        source.FromStaging.Should().BeFalse("the loadout provides this file, the game did not write it");
    }

    [Fact]
    public void ARowsMtimeReachesItsSource()
    {
        var planned = new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero);
        var row = new MountManifestRow(Root / "a.esp", Id(2), Size.FromLong(20), Id(1), false, planned);

        Build(new MountManifestView(Root, [row])).Single().MTime.Should().Be(planned);
    }

    // A vanilla master the real folder holds may have no staged copy and no xxHash3 identity, so asking the
    // staging store for it would fail the mount.
    [Fact]
    public void ARowOverBytesTheRealFolderHoldsOnlyRetimesTheRealFile()
    {
        var planned = new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero);
        var row = new MountManifestRow(Root / "a.esm", Id(1), Size.FromLong(20), Id(1), false, planned);

        var source = MountSourceBuilder.Build(new MountManifestView(Root, [row]), [], _ => throw new InvalidOperationException("nothing is staged for it")).Single();

        source.Kind.Should().Be(VfsSourceKind.Retimed);
        source.Path.Should().Be(RelativePath.FromUnsanitizedInput("a.esm"));
        source.RealPath.Should().BeNull();
        source.MTime.Should().Be(planned);
    }

    [Fact]
    public void ARowWithoutAnMtimeLeavesItsSourceUnstamped()
    {
        var row = new MountManifestRow(Root / "a.esp", Id(2), Size.FromLong(20), Id(1), false, null);

        Build(new MountManifestView(Root, [row])).Single().MTime.Should().BeNull();
    }

    [Fact]
    public void AHiddenRowHidesThePath()
    {
        var row = new MountManifestRow(Root / "old.esp", FileIdentity.None, Size.Zero, Id(1), true, null);

        var source = Build(new MountManifestView(Root, [row])).Single();

        source.Path.Should().Be(RelativePath.FromUnsanitizedInput("old.esp"));
        source.Kind.Should().Be(VfsSourceKind.Hidden);
        source.RealPath.Should().BeNull();
    }

    [Fact]
    public void ADeletedWinnerUnderTheRootIsHidden()
    {
        var source = Build(MountManifestView.Empty(Root), Root / "gone.esp").Single();

        source.Path.Should().Be(RelativePath.FromUnsanitizedInput("gone.esp"));
        source.Kind.Should().Be(VfsSourceKind.Hidden);
    }

    [Fact]
    public void ADeletedWinnerOutsideTheRootIsIgnored()
    {
        Build(MountManifestView.Empty(Root), new GamePath(LocationId.Game, "Other/gone.esp"), new GamePath(LocationId.Preferences, "Data/gone.esp"))
            .Should().BeEmpty();
    }
}
