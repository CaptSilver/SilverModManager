using FluentAssertions;
using SilverModManager.Abstractions.Games;
using Xunit;

namespace SilverModManager.Games.Generic.Tests;

/// <summary>
/// The trash follows the freedesktop.org layout. Every test gives it a data home in a temp directory, so the
/// user's own trash is never touched.
/// </summary>
public class FreedesktopTrashTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("trash-tests").FullName;
    private string DataHome => Path.Combine(_root, "data");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private string Folder(string name, string fileName = "a.ini", string contents = "x")
    {
        var path = Path.Combine(_root, "source", name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, fileName), contents);
        return path;
    }

    private FreedesktopTrash Trash(Action<string, string>? move = null)
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 1, 13, 5, 9, TimeSpan.Zero));
        return move is null ? new FreedesktopTrash(DataHome, clock) : new FreedesktopTrash(DataHome, clock, move);
    }

    [Fact]
    public void AFolderMovesIntoFilesWithItsContents_AndTheSourceIsGone()
    {
        var folder = Folder("Skyrim", "Skyrim.ini", "bUseWindow=1");

        Trash().MoveToTrash(folder);

        Directory.Exists(folder).Should().BeFalse();
        File.ReadAllText(Path.Combine(DataHome, "Trash", "files", "Skyrim", "Skyrim.ini")).Should().Be("bUseWindow=1");
    }

    [Fact]
    public void TheInfoFileNamesTheOriginalPathAndTheTimeOfDeletion()
    {
        var folder = Folder("Skyrim");

        Trash().MoveToTrash(folder);

        File.ReadAllLines(Path.Combine(DataHome, "Trash", "info", "Skyrim.trashinfo")).Should().Equal(
            "[Trash Info]",
            $"Path={folder}",
            "DeletionDate=2026-11-01T13:05:09");
    }

    [Fact]
    public void ThePathInTheInfoFileIsPercentEncoded_WithSlashesKept()
    {
        var folder = Folder("My Games & Ünï");

        Trash().MoveToTrash(folder);

        var path = File.ReadAllLines(Path.Combine(DataHome, "Trash", "info", "My Games & Ünï.trashinfo"))[1];
        path.Should().EndWith("/source/My%20Games%20%26%20%C3%9Cn%C3%AF");
        path.Should().StartWith("Path=/");
    }

    [Fact]
    public void ASecondFolderWithTheSameName_GetsAnotherNameAndKeepsTheFirst()
    {
        var first = Folder("Skyrim", contents: "first");
        Trash().MoveToTrash(first);
        var second = Folder("Skyrim", contents: "second");

        Trash().MoveToTrash(second);

        var files = Directory.GetDirectories(Path.Combine(DataHome, "Trash", "files")).Select(Path.GetFileName).Order().ToArray();
        files.Should().HaveCount(2).And.Contain("Skyrim");
        var other = files.Single(n => n != "Skyrim")!;
        File.ReadAllText(Path.Combine(DataHome, "Trash", "files", "Skyrim", "a.ini")).Should().Be("first");
        File.ReadAllText(Path.Combine(DataHome, "Trash", "files", other, "a.ini")).Should().Be("second");
        File.Exists(Path.Combine(DataHome, "Trash", "info", other + ".trashinfo")).Should().BeTrue();
    }

    [Fact]
    public void ANameAlreadyTakenInFilesWithoutAnInfoFile_IsNotOverwritten()
    {
        Directory.CreateDirectory(Path.Combine(DataHome, "Trash", "files", "Skyrim"));
        File.WriteAllText(Path.Combine(DataHome, "Trash", "files", "Skyrim", "old.txt"), "old");
        var folder = Folder("Skyrim");

        Trash().MoveToTrash(folder);

        File.ReadAllText(Path.Combine(DataHome, "Trash", "files", "Skyrim", "old.txt")).Should().Be("old");
        Directory.Exists(folder).Should().BeFalse();
    }

    [Fact]
    public void AMoveThatFails_RefusesLeavesTheFolderAndNoInfoFileBehind()
    {
        var folder = Folder("Skyrim");

        var act = () => Trash((_, _) => throw new IOException("Invalid cross-device link")).MoveToTrash(folder);

        act.Should().Throw<HostFolderMoveRefusedException>().Which.Message.Should().Contain(folder);
        File.Exists(Path.Combine(folder, "a.ini")).Should().BeTrue();
        Directory.GetFiles(Path.Combine(DataHome, "Trash", "info")).Should().BeEmpty();
    }

    [Fact]
    public void AFolderThatIsGone_RefusesAndCreatesNothing()
    {
        var act = () => Trash().MoveToTrash(Path.Combine(_root, "never-there"));

        act.Should().Throw<HostFolderMoveRefusedException>();
        Directory.Exists(Path.Combine(DataHome, "Trash", "files")).Should().BeFalse();
    }
}
