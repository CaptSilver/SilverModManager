using FluentAssertions;
using SilverModManager.Games.FromSoft.Cleanup;

namespace SilverModManager.Games.FromSoft.Tests.Cleanup;

// The copy path is what a move between the Steam library and the app's data folder takes. It is called directly here
// because whether a rename crosses filesystems depends on where the test's temp folder happens to live.
public sealed class TreeMoveTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("tree-move-");

    private string Source => Path.Combine(_root.FullName, "game", "mod");
    private string Target => Path.Combine(_root.FullName, "set-aside", "mod");

    public void Dispose()
    {
        foreach (var file in _root.EnumerateFiles("*", SearchOption.AllDirectories))
            file.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        _root.Delete(recursive: true);
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(Source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException(path));
        File.WriteAllText(path, text);
    }

    [Fact]
    public void ACopiedTreeKeepsItsFilesAndLinksAndTheSourceGoes()
    {
        Write("parts/x.dcx", "override");
        File.CreateSymbolicLink(Path.Combine(Source, "x-link.dcx"), "parts/x.dcx");
        Directory.CreateSymbolicLink(Path.Combine(Source, "parts-link"), "parts");
        Directory.CreateDirectory(Path.GetDirectoryName(Target) ?? throw new InvalidOperationException(Target));

        TreeMove.CopyThenRemove(Source, Target);

        Directory.Exists(Source).Should().BeFalse();
        File.ReadAllText(Path.Combine(Target, "parts/x.dcx")).Should().Be("override");
        new FileInfo(Path.Combine(Target, "x-link.dcx")).LinkTarget.Should().Be("parts/x.dcx");
        new DirectoryInfo(Path.Combine(Target, "parts-link")).LinkTarget.Should().Be("parts");
    }

    [Fact]
    public void ACopyThatFailsPartWayLeavesTheSourceAndNoTarget()
    {
        Write("a.dcx", "first");
        Write("parts/z.dcx", "unreadable");
        var unreadable = new FileInfo(Path.Combine(Source, "parts/z.dcx")) { UnixFileMode = UnixFileMode.None };
        Assert.SkipWhen(CanRead(unreadable.FullName), "This user reads files whatever their mode, so no copy can fail on one.");
        Directory.CreateDirectory(Path.GetDirectoryName(Target) ?? throw new InvalidOperationException(Target));

        var copy = () => TreeMove.CopyThenRemove(Source, Target);

        copy.Should().Throw<UnauthorizedAccessException>();
        Directory.Exists(Target).Should().BeFalse();
        File.ReadAllText(Path.Combine(Source, "a.dcx")).Should().Be("first");
        unreadable.UnixFileMode = UnixFileMode.UserRead;
        File.ReadAllText(unreadable.FullName).Should().Be("unreadable");
    }

    private static bool CanRead(string path)
    {
        try
        {
            using var _ = File.OpenRead(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
