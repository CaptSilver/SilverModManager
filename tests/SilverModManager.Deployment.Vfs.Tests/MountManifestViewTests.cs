using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Deployment.Vfs.Tests;

public class MountManifestViewTests
{
    private static readonly GamePath Root = new(LocationId.Game, "Data");
    private static readonly GamePath ModEsp = Root / "mod.esp";

    private static FileIdentity Id(ulong value) => FileIdentity.XxHash3(Hash.From(value));

    private static SyncNodePart Part(ulong hash, ulong size, long ticks = 7) =>
        new() { Identity = Id(hash), Size = Size.FromLong((long)size), LastModifiedTicks = ticks };

    private static MountManifestRow Row(GamePath path, ulong hash, ulong size, ulong? backing = null, bool hidden = false) =>
        new(path, Id(hash), Size.FromLong((long)size), backing is { } b ? Id(b) : null, hidden, null);

    private static List<PathPartPair> Overlay(MountManifestRow row, params PathPartPair[] state) =>
        new MountManifestView(Root, [row]).Overlay(state).ToList();

    [Fact]
    public void ARowReplacesTheRealEntryOnBothSides()
    {
        var view = new MountManifestView(Root, [Row(ModEsp, hash: 2, size: 20, backing: 1)]);
        var latest = new[] { new PathPartPair(ModEsp, Part(1, 10)) };
        var previous = new[] { new PathPartPair(ModEsp, Part(1, 10)) };

        var overlaidLatest = view.Overlay(latest).Single();
        var overlaidPrevious = view.Overlay(previous).Single();

        overlaidLatest.Part.Identity.Should().Be(Id(2));
        overlaidLatest.Part.Size.Should().Be(Size.FromLong(20));
        overlaidPrevious.Part.Should().Be(overlaidLatest.Part);
    }

    [Fact]
    public void AHiddenRowRemovesThePath()
    {
        var kept = new PathPartPair(Root / "other.esp", Part(5, 5));

        var result = Overlay(Row(ModEsp, 0, 0, backing: 1, hidden: true), new PathPartPair(ModEsp, Part(1, 10)), kept);

        result.Should().Equal(kept);
    }

    [Fact]
    public void AHiddenRowOverAChangedRealFileIsDropped()
    {
        var replaced = new PathPartPair(ModEsp, Part(9, 99));

        Overlay(Row(ModEsp, 1, 10, backing: 1, hidden: true), replaced).Should().Equal(replaced);
    }

    [Fact]
    public void AHiddenRowWithNoRealFileAddsNothing() =>
        Overlay(Row(ModEsp, 0, 0, hidden: true)).Should().BeEmpty();

    [Fact]
    public void ARowWithNoBackingIdentityAndNoRealFileIsAdded()
    {
        var result = Overlay(Row(ModEsp, hash: 2, size: 20));

        result.Should().ContainSingle().Which.Should().Match<PathPartPair>(p => p.Path == ModEsp && p.Part.Identity.Equals(Id(2)) && p.Part.Size == Size.FromLong(20));
    }

    [Fact]
    public void ARowWhoseBackingIdentityDiffersFromTheRealFileIsDropped()
    {
        var real = new PathPartPair(ModEsp, Part(9, 99));

        Overlay(Row(ModEsp, hash: 2, size: 20, backing: 1), real).Should().Equal(real);
    }

    [Fact]
    public void ARowWithNoBackingIdentityOverARealFileIsDropped()
    {
        var real = new PathPartPair(ModEsp, Part(9, 99));

        Overlay(Row(ModEsp, hash: 2, size: 20), real).Should().Equal(real);
    }

    [Fact]
    public void PathsOutsideTheRootPassThrough()
    {
        var outside = new PathPartPair(new GamePath(LocationId.Game, "Game.exe"), Part(3, 3));

        var result = Overlay(Row(ModEsp, hash: 2, size: 20, backing: 1), outside);

        result.Should().Contain(outside);
    }

    [Fact]
    public void ARowsModifiedTimeIsServed()
    {
        var mtime = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var view = new MountManifestView(Root, [Row(ModEsp, 2, 20) with { MTime = mtime }]);

        view.Overlay([]).Single().Part.LastModifiedTicks.Should().Be(mtime.UtcTicks);
    }

    [Fact]
    public void AnEmptyViewPassesEverythingThrough()
    {
        var state = new[] { new PathPartPair(ModEsp, Part(1, 1)) };

        MountManifestView.Empty(Root).Overlay(state).Should().Equal(state);
    }
}
