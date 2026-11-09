using FluentAssertions;
using SilverModManager.Backend.FileExtractor;

namespace SilverModManager.Backend.Tests.FileExtractor;

public class ArchiveEntryPlanTests
{
    private static ArchiveEntryPlan Plan(params string[] names) =>
        new(names.Select(n => new ArchiveEntryPlan.Entry(n, IsDirectory: n.EndsWith('/'))).ToArray());

    [Fact]
    public void FilesNoGameLoadsAreLeftOutOfTheBulkRun()
    {
        var plan = Plan("Data/Mod.esp", "src/", "src/skse64/main.cpp", "src/skse64/.gitignore", "src/skse64/skse64.vcxproj", "SKSE/Plugins/plugin.dll");

        plan.Excluded.Should().BeEquivalentTo("src/skse64/main.cpp", "src/skse64/.gitignore", "src/skse64/skse64.vcxproj");
        plan.Moved.Should().BeEmpty();
        plan.IsPlain.Should().BeFalse();
        plan.Kept.Select(k => System.Text.Encoding.UTF8.GetString(k.Raw)).Should().NotContain(name => name.EndsWith(".cpp"));
    }

    [Fact]
    public void ANameThatIsMovedAndNeverLoadedIsNotExtractedAtAll()
    {
        var plan = Plan("src\\main.cpp", "kept.txt");

        plan.Moved.Should().BeEmpty();
        plan.Excluded.Should().BeEquivalentTo("src\\main.cpp");
    }

    [Fact]
    public void PapyrusSourceStaysInTheRun()
    {
        var plan = Plan("Scripts/Source/Thing.psc", "Scripts/Thing.pex");

        plan.IsPlain.Should().BeTrue();
    }

    // 7z drops "." and empty segments itself, so these need neither a separate run nor a warning. Each
    // separate run decompresses the whole archive again, which a tarball of thousands of "./" entries
    // can't afford.
    [Fact]
    public void NamesThat7zPlacesTheSameWayAsTheRuleAreLeftToTheBulkRun()
    {
        var plan = Plan("./", "./a/", "./a/x.txt", "a//y.txt", "a/./z.txt");

        plan.IsPlain.Should().BeTrue();
        plan.Rewrites.IsEmpty.Should().BeTrue();
    }

    // 7z drops "/" and ".." at the front the same way, so these are reported but still left to 7z.
    [Fact]
    public void ARewriteThat7zAlreadyMatchesIsReportedButNotMoved()
    {
        var plan = Plan("../x.txt", "/y.txt", "../../z/");

        plan.IsPlain.Should().BeTrue();
        plan.Rewrites.IsEmpty.Should().BeFalse();
    }

    [Theory]
    [InlineData("a/../x.txt", "x.txt")]
    [InlineData("a\\x.txt", "a/x.txt")]
    [InlineData("C:/x.txt", "x.txt")]
    [InlineData("c:x.txt", "x.txt")]
    public void ANameThat7zPlacesElsewhereIsMovedToTheCleanedPath(string raw, string cleaned)
    {
        var plan = Plan(raw, "kept.txt");

        plan.Moved.Should().Equal((raw, cleaned));
        plan.Excluded.Should().BeEquivalentTo(raw);
    }

    [Fact]
    public void AFolderThat7zPlacesElsewhereIsCreatedDirectly()
    {
        var plan = Plan("a\\b/", "./c/");

        plan.Folders.Should().Equal("a/b");
        plan.Excluded.Should().BeEquivalentTo("a\\b/");
    }

    [Fact]
    public void AnEntryOverwrittenByADifferentNameIsLeftOut()
    {
        var plan = Plan("x.txt", "./x.txt");

        plan.Excluded.Should().BeEquivalentTo("x.txt");
        plan.Moved.Should().BeEmpty();
        plan.Rewrites.IsEmpty.Should().BeFalse();
    }

    private static ArchiveEntryPlan Plan(params ArchiveEntryPlan.Entry[] entries) => new(entries);

    private static ArchiveEntryPlan.Entry File(string name) => new(name, IsDirectory: false);
    private static ArchiveEntryPlan.Entry Symlink(string name, string? target = null) => new(name, IsDirectory: false, SymlinkTarget: target, IsSymlink: true);
    private static ArchiveEntryPlan.Entry HardLink(string name, string target) => new(name, IsDirectory: false, HardLinkTarget: target);

    [Fact]
    public void ASymlinkEntryIsExcludedFromTheBulkRunAndListedAsALink()
    {
        var plan = Plan(File("sub/ok.txt"), Symlink("../up/link", "sub/ok.txt"));

        plan.IsPlain.Should().BeFalse();
        plan.Excluded.Should().BeEquivalentTo("../up/link");
        plan.Links.Should().Equal(("../up/link", "up/link", "sub/ok.txt"));
        plan.Moved.Should().BeEmpty();
    }

    [Fact]
    public void ASymlinkDoesNotSupersedeAFileAtItsCleanedPath()
    {
        var plan = Plan(File("a.txt"), Symlink("./a.txt", "b.txt"));

        plan.Excluded.Should().BeEquivalentTo("./a.txt");
        plan.Links.Should().ContainSingle();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ANameStoredAsBothAFileAndALinkIsDroppedWhicheverComesLast(bool linkLast)
    {
        ArchiveEntryPlan.Entry[] pair = linkLast ? [File("sub/../x.txt"), Symlink("sub/../x.txt", "ok.txt")] : [Symlink("sub/../x.txt", "ok.txt"), File("sub/../x.txt")];
        var plan = Plan([.. pair, File("ok.txt")]);

        plan.Moved.Should().BeEmpty();
        plan.MovedIndices.Should().BeEmpty();
        plan.Links.Should().BeEmpty();
        plan.LinkIndices.Should().BeEmpty();
        plan.Excluded.Should().BeEquivalentTo("sub/../x.txt");
        plan.MixedLinkNames.Should().Equal("sub/../x.txt");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ANameStoredAsBothAFileAndAHardLinkIsDroppedWhicheverComesLast(bool linkLast)
    {
        ArchiveEntryPlan.Entry[] pair = linkLast ? [File("sub/../x.txt"), HardLink("sub/../x.txt", "ok.txt")] : [HardLink("sub/../x.txt", "ok.txt"), File("sub/../x.txt")];
        var plan = Plan([File("ok.txt"), .. pair]);

        plan.Moved.Should().BeEmpty();
        plan.DroppedHardLinks.Should().BeEmpty();
        plan.Excluded.Should().BeEquivalentTo("sub/../x.txt");
        plan.MixedLinkNames.Should().Equal("sub/../x.txt");
    }

    [Fact]
    public void ADroppedMixedNameDoesNotSupersedeAnotherEntryAtItsPath()
    {
        var plan = Plan(File("x.txt"), File("a/../x.txt"), Symlink("a/../x.txt", "ok.txt"));

        plan.Excluded.Should().BeEquivalentTo("a/../x.txt");
        plan.Kept.Should().ContainSingle(k => k.Index == 0);
    }

    [Fact]
    public void ARepeatedLinkNameAloneIsNotMixed()
    {
        var plan = Plan(Symlink("a/../l", "x"), Symlink("a/../l", "y"));

        plan.Links.Should().HaveCount(2);
        plan.MixedLinkNames.Should().BeEmpty();
    }

    [Fact]
    public void AnEntryBelowASymlinkIsExtractedAloneBecauseExcludingTheLinkWouldExcludeIt()
    {
        var plan = Plan(Symlink("link", "/outside"), File("link/escaped.txt"));

        plan.Moved.Should().Equal(("link/escaped.txt", "link/escaped.txt"));
    }

    [Fact]
    public void AHardLinkWithoutAKeptTargetIsExcluded()
    {
        var plan = Plan(HardLink("stolen.txt", "/outside/secret.txt"), File("ok.txt"));

        plan.Excluded.Should().BeEquivalentTo("stolen.txt");
        plan.DroppedHardLinks.Should().ContainSingle(d => d.Name == "stolen.txt");
    }

    [Fact]
    public void AHardLinkToAKeptEntryIsLeftToTheBulkRun()
    {
        var plan = Plan(File("sub/ok.txt"), HardLink("hard.txt", "sub/ok.txt"));

        plan.IsPlain.Should().BeTrue();
        plan.DroppedHardLinks.Should().BeEmpty();
    }

    [Fact]
    public void AHardLinkToAMovedEntryIsDropped()
    {
        var plan = Plan(File("a/../x.txt"), HardLink("hard.txt", "a/../x.txt"));

        plan.Excluded.Should().Contain("hard.txt");
        plan.DroppedHardLinks.Should().ContainSingle(d => d.Name == "hard.txt");
    }

    [Fact]
    public void AHardLinkToALaterEntryIsDropped()
    {
        var plan = Plan(HardLink("hard.txt", "ok.txt"), File("ok.txt"));

        plan.DroppedHardLinks.Should().ContainSingle(d => d.Name == "hard.txt");
    }

    [Fact]
    public void AHardLinkWithARewrittenNameIsDropped()
    {
        var plan = Plan(File("ok.txt"), HardLink("a\\hard.txt", "ok.txt"));

        plan.DroppedHardLinks.Should().ContainSingle(d => d.Name == "a\\hard.txt");
    }

    private static ArchiveEntryPlan.Entry Raw(byte[] raw, bool isDirectory = false) => new(EntryNames.Decode(raw), isDirectory, RawName: raw);

    // 7z places "trailing space /f.txt" in the right shape and only the trailing space is wrong, so the
    // entry is left in the bulk run and renamed afterwards.
    [Fact]
    public void ATrailingSpaceDoesNotTakeAnEntryOutOfTheBulkRun()
    {
        var plan = Plan("trailing space /file.txt", "end.txt ");

        plan.IsPlain.Should().BeTrue();
        plan.Kept.Select(k => k.Index).Should().Equal(0, 1);
    }

    [Fact]
    public void ATrailingSpaceEntryThatNeedsAnotherFixIsMoved()
    {
        var plan = Plan("a\\y ", "../x.txt ");

        plan.Moved.Should().Equal(("a\\y ", "a/y"));
        plan.Excluded.Should().BeEquivalentTo("a\\y ");
    }

    [Fact]
    public void TwoNamesThatTrimToOnePathKeepTheLaterOne()
    {
        var plan = Plan("a ", "a");

        plan.Excluded.Should().BeEquivalentTo("a ");
        plan.Rewrites.IsEmpty.Should().BeFalse();
        plan.Kept.Select(k => k.Index).Should().Equal(1);
    }

    [Fact]
    public void ACodePage437NameIsDecodedForThePathButKeptAsStoredForExtraction()
    {
        byte[] raw = [(byte)'a', 0x8B, (byte)'/', (byte)'f'];
        var plan = Plan(Raw(raw), Raw("sub\\x"u8.ToArray()));

        plan.Kept.Should().ContainSingle(k => k.Index == 0).Which.Raw.Should().Equal(raw);
        plan.Moved.Should().Equal(("sub\\x", "sub/x"));
        plan.RawName(plan.MovedIndices.Single()).Should().Equal("sub\\x"u8.ToArray());
    }

    [Fact]
    public void ANameThatCannotBeSelectedStaysInTheBulkRunWhateverElseIsTrueOfIt()
    {
        byte[] raw = [(byte)'.', (byte)'.', (byte)'/', 0x8B, (byte)' '];
        var plan = Plan(Raw(raw), Symlink("link", "/etc/passwd"), File("ok.txt"));

        plan.UnaddressableCount.Should().Be(1);
        plan.Kept.Should().Contain(k => k.Index == 0);
        plan.Excluded.Should().BeEquivalentTo("link");
        plan.Moved.Should().BeEmpty();
    }

    [Fact]
    public void AnUnaddressableSymlinkIsNotListedAsALink()
    {
        byte[] raw = [(byte)'l', 0x8B, (byte)' '];
        var plan = Plan(new ArchiveEntryPlan.Entry(EntryNames.Decode(raw), false, SymlinkTarget: "/etc/passwd", IsSymlink: true, RawName: raw));

        plan.Links.Should().BeEmpty();
        plan.Excluded.Should().BeEmpty();
        plan.Kept.Should().ContainSingle();
    }

    [Fact]
    public void AnUnaddressableSymlinkOrHardLinkIsMarkedToBeDropped()
    {
        byte[] symlinkName = [(byte)'s', 0x8B, (byte)' '];
        byte[] hardName = [(byte)'h', 0x8B, (byte)' '];
        byte[] fileName = [(byte)'f', 0x8B, (byte)' '];
        var plan = Plan(
            new ArchiveEntryPlan.Entry(EntryNames.Decode(symlinkName), false, SymlinkTarget: "x", IsSymlink: true, RawName: symlinkName),
            new ArchiveEntryPlan.Entry(EntryNames.Decode(hardName), false, HardLinkTarget: "f", RawName: hardName),
            Raw(fileName));

        plan.Kept.Select(k => k.Drop).Should().Equal(true, true, false);
    }

    // 7z cannot be told to leave such a name out, so it is extracted with the rest and removed again afterwards.
    [Fact]
    public void AnUnaddressableSourceFileIsMarkedToBeDropped()
    {
        byte[] sourceName = [.. "src/"u8.ToArray(), 0x8B, .. "main.cpp "u8.ToArray()];
        byte[] otherName = [.. "Data/"u8.ToArray(), 0x8B, .. "mod.esp "u8.ToArray()];
        var plan = Plan(Raw(sourceName), Raw(otherName));

        plan.UnaddressableCount.Should().Be(2);
        plan.Kept.Select(k => k.Drop).Should().Equal(true, false);
    }

    [Fact]
    public void ARepeatedNameIsReportedAsRepeated()
    {
        var plan = Plan("a\\x.txt", "a\\x.txt", "y.txt");

        plan.IsRepeated(0).Should().BeTrue();
        plan.IsRepeated(2).Should().BeFalse();
        plan.Moved.Should().ContainSingle();
    }

    [Fact]
    public void TheEntriesBelowAFolderAreFoundByTheirStoredBytes()
    {
        var plan = Plan("link", "link/a", "linked", "link/b/c");

        plan.IndicesBelow(0).Should().Equal(1, 3);
    }
}
