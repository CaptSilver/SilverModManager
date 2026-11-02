using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers.FolderCase;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests.FolderCase;

public class FolderSpellingsTests
{
    private static GamePath G(string path) => new(LocationId.Game, RelativePath.FromUnsanitizedInput(path));

    private static FolderSpellings Build(string[]? fixedFolders = null, bool allTiers = true, string[]? folders = null,
        (string Path, bool Vanilla)[]? files = null, string[]? targets = null)
        => FolderSpellings.Build(new FolderSpellingSources(
            fixedFolders ?? [],
            allTiers,
            (folders ?? []).Select(G).ToArray(),
            (files ?? []).Select(f => (G(f.Path), f.Vanilla)).ToArray(),
            (targets ?? []).Select(G).ToArray()));

    private static string Folder(FolderSpellings s, string path) => s.CanonicalFolder(LocationId.Game, path);

    [Fact]
    public void Fixed_WinsOverDiskAndLoadout()
    {
        var s = Build(["Mods"], files: [("mods/a.ws", false), ("mods/b.ws", false)], targets: ["MODS/c.ws"]);

        s.Canonical(G("mods/a.ws")).Path.ToString().Should().Be("Mods/a.ws");
        s.Canonical(G("MODS/x/y.ws")).Path.ToString().Should().Be("Mods/x/y.ws");
    }

    [Fact]
    public void VanillaOnDisk_WinsOverAnotherOnDisk()
    {
        var s = Build(files: [("Data/Interface/v.swf", true), ("Data/interface/a.swf", false), ("Data/interface/b.swf", false)]);

        Folder(s, "Data/interface").Should().Be("Data/Interface");
        Folder(s, "Data/INTERFACE").Should().Be("Data/Interface");
    }

    [Fact]
    public void AttestedSpellingAbsentFromDisk_DoesNotWin()
    {
        // The store's spelling only counts when it is the spelling on disk: the tier looks at disk rows.
        var s = Build(files: [("Data/interface/a.swf", false)], targets: ["Data/Interface/x.swf"]);

        Folder(s, "Data/Interface").Should().Be("Data/interface");
    }

    [Fact]
    public void TwoVanillaSpellings_MarkTheGroupGameOwned()
    {
        var s = Build(files: [("Data/Interface/v.swf", true), ("Data/interface/w.swf", true)]);

        s.IsGameOwned(LocationId.Game, "Data/Interface").Should().BeTrue();
        Folder(s, "Data/interface").Should().Be("Data/interface", "a game-owned group keeps the spelling it was written in");
    }

    [Fact]
    public void OnDisk_WinsOverLoadoutOnly()
    {
        var s = Build(files: [("mods/own/user.ws", false)], targets: ["Mods/a/1.ws", "Mods/a/2.ws", "Mods/b/3.ws"]);

        Folder(s, "Mods/a").Should().Be("mods/a");
    }

    [Fact]
    public void TwoOnDisk_MostFilesWins_TieBreaksOrdinal()
    {
        var most = Build(files: [("a/x", false), ("A/y", false), ("A/z", false)]);
        Folder(most, "a").Should().Be("A");

        var tie = Build(files: [("a/x", false), ("A/y", false)]);
        Folder(tie, "a").Should().Be("A", "ordinal order puts the upper-case spelling first");
    }

    [Fact]
    public void LoadoutOnly_MostFilesWins_TieBreaksOrdinal()
    {
        var most = Build(targets: ["a/1", "a/2", "A/3"]);
        Folder(most, "A").Should().Be("a");

        var tie = Build(targets: ["a/1", "A/2"]);
        Folder(tie, "a").Should().Be("A");
    }

    [Fact]
    public void Canonical_FoldsEveryFolderSegment()
    {
        var s = Build(targets: ["Data/Scripts/Source/a.psc", "Data/Scripts/Source/b.psc", "data/scripts/source/c.psc", "DATA/Scripts/source/d.psc"]);

        s.Canonical(G("DATA/SCRIPTS/SOURCE/e.psc")).Path.ToString().Should().Be("Data/Scripts/Source/e.psc");
        s.IsCanonical(G("Data/Scripts/Source/a.psc")).Should().BeTrue();
        s.IsCanonical(G("data/scripts/source/c.psc")).Should().BeFalse();
    }

    [Fact]
    public void Canonical_FoldsLeafNamesAcrossRows()
    {
        var s = Build(files: [("Data/Skyrim.esm", true)], targets: ["Data/skyrim.esm", "Data/Foo.dds", "data/foo.dds", "Data/foo.dds"]);

        s.Canonical(G("Data/skyrim.esm")).Path.ToString().Should().Be("Data/Skyrim.esm", "the disk's spelling wins");
        s.Canonical(G("Data/Foo.dds")).Path.ToString().Should().Be("Data/foo.dds", "most rows wins when nothing is on disk");
    }

    [Fact]
    public void Canonical_KeysByFoldedParent()
    {
        // The rows spell the parent differently at each level; the deepest folder still resolves in one lookup.
        var s = Build(targets: ["A/B/c/1", "a/B/c/2", "a/b/C/3", "a/b/C/4", "a/b/C/5"]);

        s.Canonical(G("A/B/c/x")).Path.ToString().Should().Be("a/b/C/x");
        s.Canonical(G("a/b/c/x")).Path.ToString().Should().Be("a/b/C/x");
    }

    [Fact]
    public void NotUnderWine_FoldsOnlyFixed()
    {
        var s = Build(["Mods"], allTiers: false, files: [("Data/Interface/a", false)], targets: ["Data/interface/b", "mods/x"]);

        s.Canonical(G("Data/interface/b")).Path.ToString().Should().Be("Data/interface/b");
        s.Canonical(G("mods/x")).Path.ToString().Should().Be("Mods/x");
    }

    [Fact]
    public void Build_280kPaths_CacheHitsPerFolder()
    {
        var targets = new List<string>();
        for (var folder = 0; folder < 1000; folder++)
        {
            for (var file = 0; file < 280; file++) targets.Add($"Data/f{folder}/sub/file{file}.dds");
        }
        var s = Build(targets: targets.ToArray());

        foreach (var target in targets) s.Canonical(G(target));

        // One miss per distinct folder; every other path of the folder is a hit.
        s.CacheHits.Should().Be(targets.Count - 1000);
    }
}
