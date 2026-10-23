using FluentAssertions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Deployment.Vfs.Tests;

public class VfsSourcesRuleTests
{
    private static readonly GamePath Root = new(LocationId.Game, "Data");
    private static readonly AbsolutePath StagingRoot = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine("vfs-sources-rule-tests");

    private static AbsolutePath StagedPathFor(Hash hash) => StagingRoot.Combine(hash.ToString());

    private static SyncNodePart Part(ulong hash, ulong size) => new()
    {
        Hash = Hash.From(hash),
        Size = Size.From(size),
        LastModifiedTicks = 0,
    };

    private static SyncNode Node(LoadoutSourceItemType type, SyncNodePart loadout = default, SyncNodePart disk = default) => new()
    {
        SourceItemType = type,
        Loadout = loadout,
        Disk = disk,
    };

    private static GamePath P(string path, LocationId? location = null) => new(location ?? LocationId.Game, path);

    private static VfsSourceSet Run(Dictionary<GamePath, SyncNode> tree, params GamePath[] deleted)
        => VfsSources.FromSyncTree(tree, deleted, Root, StagedPathFor);

    [Fact]
    public void ALoadoutFileIsStagedAtTheStorePathOfItsHash()
    {
        var tree = new Dictionary<GamePath, SyncNode> { [P("Data/mod/a.pak")] = Node(LoadoutSourceItemType.Loadout, Part(0xA1, 12)) };

        var set = Run(tree);

        set.Sources.Should().ContainSingle().Which.Should().Be(new VfsFileSource(
            RelativePath.FromUnsanitizedInput("mod/a.pak"),
            VfsSourceKind.Staged,
            StagedPathFor(Hash.From(0xA1)),
            Size.From(12),
            Hash.From(0xA1)));
        set.Unserved.Should().BeEmpty();
    }

    [Fact]
    public void AVanillaFileIsServedFromTheBackingDirectory()
    {
        var tree = new Dictionary<GamePath, SyncNode> { [P("Data/image.dds")] = Node(LoadoutSourceItemType.Game, Part(0xB1, 3)) };

        var set = Run(tree);

        set.Sources.Should().BeEmpty();
        set.Unserved.Should().BeEmpty();
    }

    [Fact]
    public void ADiskOnlyNodeIsServedFromTheBackingDirectoryAndNotHidden()
    {
        // MergeStates adds nodes for files the game wrote itself with the default item type and no loadout hash.
        var tree = new Dictionary<GamePath, SyncNode> { [P("Data/made-by-game.log")] = Node(LoadoutSourceItemType.Loadout, disk: Part(0xC1, 9)) };

        var set = Run(tree);

        set.Sources.Should().BeEmpty();
        set.Unserved.Should().BeEmpty();
    }

    [Fact]
    public void AnIntrinsicNodeIsReportedAsUnserved()
    {
        var tree = new Dictionary<GamePath, SyncNode> { [P("Data/generated.ini")] = Node(LoadoutSourceItemType.Intrinsic) };

        var set = Run(tree);

        set.Sources.Should().BeEmpty();
        set.Unserved.Should().Equal(P("Data/generated.ini"));
    }

    [Fact]
    public void ADeletedWinnerIsHidden()
    {
        var set = Run([], P("Data/image.dds"));

        set.Sources.Should().ContainSingle().Which.Should().Match<VfsFileSource>(s =>
            s.Path == RelativePath.FromUnsanitizedInput("image.dds") && s.Kind == VfsSourceKind.Hidden);
    }

    [Fact]
    public void ADeletedWinnerOutsideTheRootIsIgnored()
    {
        var set = Run([], P("README.txt"), P("Data/x.txt", LocationId.Preferences));

        set.Sources.Should().BeEmpty();
    }

    [Fact]
    public void ARootIsClaimedByWholeSegmentsAndWithoutRegardToCase()
    {
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [P("DataX/x.pak")] = Node(LoadoutSourceItemType.Loadout, Part(0xD1, 1)),
            [P("DATA/x.pak")] = Node(LoadoutSourceItemType.Loadout, Part(0xD2, 2)),
            [P("README.txt")] = Node(LoadoutSourceItemType.Loadout, Part(0xD3, 3)),
            [P("Data/y.pak", LocationId.Preferences)] = Node(LoadoutSourceItemType.Loadout, Part(0xD4, 4)),
        };

        var set = Run(tree);

        set.Sources.Select(s => s.Path.ToString()).Should().Equal("x.pak");
        set.Sources[0].Hash.Should().Be(Hash.From(0xD2));
    }

    [Fact]
    public void AnUnservedIntrinsicOutsideTheRootIsNotReported()
    {
        var tree = new Dictionary<GamePath, SyncNode> { [P("README.txt")] = Node(LoadoutSourceItemType.Intrinsic) };

        Run(tree).Unserved.Should().BeEmpty();
    }
}
