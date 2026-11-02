using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Wabbajack.Install;

namespace SilverModManager.Wabbajack.Tests.Install;

public class GroupFilesTests
{
    private static GamePath Path(string relative) => new(LocationId.Game, relative);

    private static FileIdentity Identity(ulong value) => FileIdentity.XxHash3(Hash.From(value));

    [Fact]
    public void AtPath_ListsEveryRowAtThePathWhateverItsHash()
    {
        var files = new GroupFiles();
        var old = files.Add(Path("a/x.esp"), Identity(1), new KnownFile());
        var current = files.Add(Path("a/x.esp"), Identity(2), new KnownFile());
        files.Add(Path("a/y.esp"), Identity(3), new KnownFile());

        var atPath = files.AtPath(Path("a/x.esp")).Select(row => row.Known).ToList();
        atPath.Should().HaveCount(2).And.Contain(old).And.Contain(current);
    }

    [Fact]
    public void AtPath_IsEmptyForAPathNothingWasWrittenTo()
        => new GroupFiles().AtPath(Path("none.esp")).Should().BeEmpty();

    [Fact]
    public void TryGet_FindsTheRowByPathAndIdentity()
    {
        var files = new GroupFiles();
        var known = files.Add(Path("a/x.esp"), Identity(1), new KnownFile());

        files.TryGet(Path("a/x.esp"), Identity(1), out var found).Should().BeTrue();
        found.Should().BeSameAs(known);
        files.TryGet(Path("a/x.esp"), Identity(2), out _).Should().BeFalse();
    }

    [Fact]
    public void Known_ListsEveryRowOnce()
    {
        var files = new GroupFiles();
        files.Add(Path("a"), Identity(1), new KnownFile());
        files.Add(Path("a"), Identity(2), new KnownFile());
        files.Add(Path("b"), Identity(1), new KnownFile());

        files.Known.Should().HaveCount(3);
    }
}
