using FluentAssertions;
using Microsoft.Extensions.Logging;
using SilverModManager.Backend.FileExtractor.Extractors;

namespace SilverModManager.Backend.Tests.FileExtractor;

public class ExtractedLinksTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("extracted-links-").FullName;
    private readonly string _root;
    private readonly string _outside;
    private readonly List<string> _log = [];

    public ExtractedLinksTests()
    {
        _root = Path.Combine(_base, "extracted");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "private");
    }

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private string InRoot(string path) => Path.Combine(_root, path);

    private void Link(string name, string target)
    {
        var path = InRoot(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.CreateSymbolicLink(path, target);
    }

    private const string Archive = "Some Mod-123-1-0.7z";

    private void Sanitize() => ExtractedLinks.Sanitize(_root, Archive, new ListLogger(_log));

    private bool Exists(string path) => File.Exists(InRoot(path)) || Directory.Exists(InRoot(path)) || new FileInfo(InRoot(path)).LinkTarget is not null;

    private void AssertSecretUntouched() => File.ReadAllText(Path.Combine(_outside, "secret.txt")).Should().Be("private");

    [Fact]
    public void AbsoluteLinkOutsideIsDropped()
    {
        Link("abs", Path.Combine(_outside, "secret.txt"));
        Sanitize();
        Exists("abs").Should().BeFalse();
        AssertSecretUntouched();
    }

    [Fact]
    public void RelativeLinkOutsideIsDropped()
    {
        Link("up", "../outside/secret.txt");
        Link("deep/up", "../../outside/secret.txt");
        Sanitize();
        Exists("up").Should().BeFalse();
        Exists("deep/up").Should().BeFalse();
        AssertSecretUntouched();
    }

    [Fact]
    public void LinkToFolderOutsideIsDroppedAndNeverFollowed()
    {
        Link("dir", _outside);
        Sanitize();
        Exists("dir").Should().BeFalse();
        File.Exists(Path.Combine(_outside, "secret.txt")).Should().BeTrue();
    }

    [Fact]
    public void LinkThatLooksInsideButResolvesOutsideThroughAnotherLinkIsDropped()
    {
        Link("out", _outside);
        Link("sneaky", "out/secret.txt");
        Sanitize();
        Exists("out").Should().BeFalse();
        Exists("sneaky").Should().BeFalse();
        AssertSecretUntouched();
    }

    [Fact]
    public void DotDotAfterAFolderLinkStepsOutOfTheLinksTarget()
    {
        // Lexically "real/../note.txt" is inside the folder, but "real" points at a folder in the outside
        // tree, so ".." lands next to secret.txt.
        File.WriteAllText(Path.Combine(_base, "note.txt"), "outside note");
        Directory.CreateDirectory(Path.Combine(_outside, "sub"));
        Link("real", Path.Combine(_outside, "sub"));
        Link("via", "real/../secret.txt");
        Sanitize();
        Exists("via").Should().BeFalse();
        AssertSecretUntouched();
    }

    [Fact]
    public void InternalFileLinkBecomesACopy()
    {
        File.WriteAllText(InRoot("real.txt"), "real content");
        Link("link", "real.txt");
        Link("chain", "link");
        Directory.CreateDirectory(InRoot("sub"));
        Link("sub/up", "../real.txt");
        Sanitize();

        foreach (var name in new[] { "link", "chain", "sub/up" })
        {
            new FileInfo(InRoot(name)).LinkTarget.Should().BeNull(name);
            File.ReadAllText(InRoot(name)).Should().Be("real content", name);
        }
    }

    [Fact]
    public void CopyIsIndependentOfTheOriginal()
    {
        File.WriteAllText(InRoot("real.txt"), "real content");
        Link("link", "real.txt");
        Sanitize();
        File.WriteAllText(InRoot("link"), "changed");
        File.ReadAllText(InRoot("real.txt")).Should().Be("real content");
    }

    [Fact]
    public void InternalFolderLinkIsDroppedAndItsTargetKept()
    {
        Directory.CreateDirectory(InRoot("sub"));
        File.WriteAllText(InRoot("sub/inner.txt"), "inner");
        Link("dir", "sub");
        Link("self", ".");
        Sanitize();

        Exists("dir").Should().BeFalse();
        Exists("self").Should().BeFalse();
        File.ReadAllText(InRoot("sub/inner.txt")).Should().Be("inner");
    }

    [Fact]
    public void DanglingAndLoopingLinksAreDropped()
    {
        Link("dangling", "missing.txt");
        Link("a", "b");
        Link("b", "a");
        Sanitize();
        Exists("dangling").Should().BeFalse();
        Exists("a").Should().BeFalse();
        Exists("b").Should().BeFalse();
    }

    [Fact]
    public void EachDroppedLinkIsLoggedWithItsEntryName()
    {
        Link("deep/escape", "../../outside/secret.txt");
        Sanitize();
        _log.Should().ContainSingle(m => m.Contains("deep/escape"));
    }

    [Fact]
    public void EveryLinkWarningNamesTheArchive()
    {
        File.WriteAllText(InRoot("real.txt"), "real content");
        Link("copied", "real.txt");
        Link("escape", "../outside/secret.txt");
        Sanitize();
        _log.Should().HaveCount(2).And.OnlyContain(m => m.Contains(Archive));
    }

    [Fact]
    public void FolderWithoutLinksIsLeftAlone()
    {
        File.WriteAllText(InRoot("real.txt"), "real content");
        Sanitize();
        File.ReadAllText(InRoot("real.txt")).Should().Be("real content");
        _log.Should().BeEmpty();
    }

    [Fact]
    public void RealPathStepsOutOfALinksTargetOnDotDot()
    {
        Directory.CreateDirectory(InRoot("a/b"));
        Link("l", "a/b");
        ExtractedLinks.RealPath(InRoot("l/../x")).Should().Be(InRoot("a/x"));
    }

    private sealed class ListLogger(List<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => messages.Add(formatter(state, exception));
    }
}
