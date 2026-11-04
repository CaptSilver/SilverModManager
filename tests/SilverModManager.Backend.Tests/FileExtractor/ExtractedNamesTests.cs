using System.Runtime.InteropServices;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using SilverModManager.Backend.FileExtractor;

namespace SilverModManager.Backend.Tests.FileExtractor;

public partial class ExtractedNamesTests : IDisposable
{
    private readonly string _root = Path.Combine(TestScratch.Root, $"ExtractedNames-{Guid.NewGuid()}");

    public ExtractedNamesTests() => Directory.CreateDirectory(_root);

    // A test that leaves a raw-byte name behind has a folder .NET can't delete.
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            System.Diagnostics.Process.Start("rm", ["-rf", _root]).WaitForExit();
        }
    }

    [LibraryImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static partial int Mkdir(byte[] path, uint mode);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
    private static partial int Open(byte[] path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "write")]
    private static partial nint Write(int fd, byte[] data, nint count);

    private byte[] RawPath(byte[] name) => RawSymlink.RawPath(_root, name);

    // .NET cannot create a name that is not valid UTF-8, so the tree 7z would leave is built with libc.
    private void MakeFolder(byte[] rawName) => Mkdir(RawPath(rawName), 0x1ED).Should().Be(0);

    private void MakeFile(byte[] rawName, string content)
    {
        var fd = Open(RawPath(rawName), 0x41 | 0x200, 0x1A4); // O_WRONLY | O_CREAT | O_TRUNC
        fd.Should().BeGreaterThan(-1);
        var data = Encoding.UTF8.GetBytes(content);
        Write(fd, data, data.Length);
        Close(fd);
    }

    private static byte[] Bytes(string name) => Encoding.Latin1.GetBytes(name);

    private string[] Tree() => Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
        .Select(p => Path.GetRelativePath(_root, p)).Order().ToArray();

    private string Read(string relative) => System.IO.File.ReadAllText(Path.Combine(_root, relative));

    private readonly List<string> _warnings = [];

    private void Repair(params ExtractedNames.Kept[] kept) =>
        ExtractedNames.Repair(_root, kept, new ListLogger(_warnings), "test.zip");

    private static ExtractedNames.Kept File(string rawLatin1, int index) => new(Bytes(rawLatin1), index, IsDirectory: false);
    private static ExtractedNames.Kept Folder(string rawLatin1, int index) => new(Bytes(rawLatin1), index, IsDirectory: true);

    private static ExtractedNames.Kept Link(string rawLatin1, int index) => new(Bytes(rawLatin1), index, IsDirectory: false, IsSymlink: true);

    // 7z leaves a 0-byte file where it refused to create a link; it must not be renamed into the result.
    [Fact]
    public void AnEmptyFileLeftForARefusedLinkIsRemoved()
    {
        MakeFile(Bytes("via\u008B "), "");

        Repair(Link("via\u008B ", 0));

        Tree().Should().BeEmpty();
    }

    [Fact]
    public void ALinkStoredAfterAFileAtTheSamePathDoesNotReplaceIt()
    {
        MakeFile(Bytes("same\u008B"), "real");
        RawSymlink.Create(_root, Bytes("same\u008B "), "elsewhere");

        Repair(File("same\u008B", 0), Link("same\u008B ", 1));

        Tree().Should().Equal("sameï");
        Read("sameï").Should().Be("real");
    }

    [Fact]
    public void AnUnaddressableLinkIsRemovedWhateverItPointsAt()
    {
        RawSymlink.Create(_root, Bytes("s\u008B "), "elsewhere");
        MakeFile(Bytes("keep.txt"), "kept");

        Repair(new ExtractedNames.Kept(Bytes("s\u008B "), 0, IsDirectory: false, IsSymlink: true, Drop: true));

        Tree().Should().Equal("keep.txt");
        _warnings.Should().ContainSingle(w => w.Contains("test.zip"));
    }

    // 7z keeps a backslash in a name as part of the file name; the entry goes where ArchiveEntryPath puts it.
    [Fact]
    public void ABackslashNameIsRenamedToTheCleanedPath()
    {
        MakeFile(Bytes("a\\b\u008B"), "data");

        Repair(File("a\\b\u008B", 0));

        Tree().Should().Equal("a", "a/b\u00EF");
    }

    [Fact]
    public void ANameWithADotDotSegmentIsRenamedToTheCleanedPath()
    {
        MakeFile(Bytes("x\\..\\y\u008B"), "data");

        Repair(File("x\\..\\y\u008B", 0));

        Tree().Should().Equal("y\u00EF");
    }

    // 7z places "a/b/../c" at a/b/c, but the cleaned path is a/c. The folder a/b is still the archive's own.
    [Fact]
    public void AFolderThatHoldsAnotherEntryIsNotRemovedWhenADotDotNameLeavesIt()
    {
        MakeFolder(Bytes("a"));
        MakeFolder(Bytes("a/b"));
        MakeFile(Bytes("a/b/keep.txt"), "kept");
        MakeFile(Bytes("a/b/c\u008B "), "data");

        Repair(File("a/b/keep.txt", 0), File("a/b/../c\u008B ", 1));

        Tree().Should().Equal("a", "a/b", "a/b/keep.txt", "a/c\u00EF");
    }

    [Fact]
    public void AFolderEntryIsNotRemovedWhenADotDotNameLeavesIt()
    {
        MakeFolder(Bytes("a"));
        MakeFolder(Bytes("a/b"));
        MakeFile(Bytes("a/b/c\u008B "), "data");

        Repair(Folder("a/b/", 0), File("a/b/../c\u008B ", 1));

        Tree().Should().Equal("a", "a/b", "a/c\u00EF");
    }

    [Fact]
    public void AnEntryWhoseFolderIsALinkIsNotRenamedThroughIt()
    {
        var outside = Path.Combine(_root, "..", $"outside-{Guid.NewGuid()}");
        Directory.CreateDirectory(outside);
        try
        {
            System.IO.File.CreateSymbolicLink(Path.Combine(_root, "d"), outside);
            MakeFile(Bytes("d\\f\u008B"), "data");

            Repair(File("d\\f\u008B", 0));

            Directory.GetFileSystemEntries(outside).Should().BeEmpty();
            Tree().Should().Equal("d");
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void ACodePage437FileNameIsRenamedToItsDecodedName()
    {
        MakeFile(Bytes("a_b\u008B\u0094.txt"), "data");

        Repair(File("a_b\u008B\u0094.txt", 0));

        Tree().Should().Equal("a_bïö.txt");
        Read("a_bïö.txt").Should().Be("data");
        _warnings.Should().ContainSingle(w => w.Contains("test.zip"));
    }

    [Fact]
    public void ARawFolderIsRenamedAndTheFilesInsideMoveWithIt()
    {
        MakeFolder(Bytes("dir_\u008B"));
        MakeFile(Bytes("dir_\u008B/inner.txt"), "inner");

        Repair(Folder("dir_\u008B/", 0), File("dir_\u008B/inner.txt", 1));

        Tree().Should().Equal("dir_ï", Path.Combine("dir_ï", "inner.txt"));
        Read("dir_ï/inner.txt").Should().Be("inner");
    }

    [Fact]
    public void ATrailingSpaceInAFolderNameIsTrimmedWithoutAFolderEntry()
    {
        Directory.CreateDirectory(Path.Combine(_root, "trailing space "));
        System.IO.File.WriteAllText(Path.Combine(_root, "trailing space ", "file.txt"), "x");

        Repair(File("trailing space /file.txt", 0));

        Tree().Should().Equal("trailing space", Path.Combine("trailing space", "file.txt"));
    }

    [Fact]
    public void AFolderThatFixesOntoAnExistingFolderIsMerged()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a"));
        System.IO.File.WriteAllText(Path.Combine(_root, "a", "y.txt"), "y");
        Directory.CreateDirectory(Path.Combine(_root, "a "));
        System.IO.File.WriteAllText(Path.Combine(_root, "a ", "x.txt"), "x");

        Repair(File("a /x.txt", 0), File("a/y.txt", 1));

        Tree().Should().Equal("a", Path.Combine("a", "x.txt"), Path.Combine("a", "y.txt"));
    }

    [Fact]
    public void WhenTwoFilesFixToOnePathTheHigherIndexStays()
    {
        System.IO.File.WriteAllText(Path.Combine(_root, "a "), "first");
        System.IO.File.WriteAllText(Path.Combine(_root, "a"), "second");

        Repair(File("a ", 0), File("a", 1));

        Tree().Should().Equal("a");
        Read("a").Should().Be("second");
        _warnings.Should().Contain(w => w.Contains("later entry"));
    }

    [Fact]
    public void ALaterRenamedFileReplacesAnEarlierUnchangedOne()
    {
        System.IO.File.WriteAllText(Path.Combine(_root, "a"), "first");
        System.IO.File.WriteAllText(Path.Combine(_root, "a "), "second");

        Repair(File("a", 0), File("a ", 1));

        Tree().Should().Equal("a");
        Read("a").Should().Be("second");
    }

    // Two entries with one stored name are one file on disk, written by the last of them.
    [Fact]
    public void ARepeatedStoredNameIsRenamedOnce()
    {
        System.IO.File.WriteAllText(Path.Combine(_root, "r "), "last");

        Repair(File("r ", 0), File("r ", 1));

        Tree().Should().Equal("r");
        Read("r").Should().Be("last");
    }

    [Fact]
    public void ControlCharactersBecomeUnderscores()
    {
        System.IO.File.WriteAllText(Path.Combine(_root, "bad_\u0001\u0002.txt"), "x");

        Repair(File("bad_\u0001\u0002.txt", 0));

        Tree().Should().Equal("bad___.txt");
    }

    [Fact]
    public void NamesThatNeedNoFixAreNotTouchedOrReported()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        System.IO.File.WriteAllText(Path.Combine(_root, "sub", "ok.txt"), "x");

        Repair(Folder("sub/", 0), File("sub/ok.txt", 1));

        Tree().Should().Equal("sub", Path.Combine("sub", "ok.txt"));
        _warnings.Should().BeEmpty();
    }

    [Fact]
    public void ANonUtfEightNameEndingInASpaceIsDecodedAndTrimmed()
    {
        MakeFile(Bytes("end_\u008B "), "x");

        Repair(File("end_\u008B ", 0));

        Tree().Should().Equal("end_ï");
    }

    [Fact]
    public void AFolderThatStillHoldsUnlistedFilesIsReportedNotLeftBehind()
    {
        MakeFolder(Bytes("dir_\u008B"));
        MakeFile(Bytes("dir_\u008B/inner.txt"), "inner");
        MakeFile(Bytes("dir_\u008B/stray.txt"), "stray");

        var act = () => Repair(File("dir_\u008B/inner.txt", 0));

        act.Should().Throw<FileExtractionException>().WithMessage("*still holds files*");
    }

    private sealed class ListLogger(List<string> warnings) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) warnings.Add(formatter(state, exception));
        }
    }
}
