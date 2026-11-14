using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Backend.Process;

namespace SilverModManager.Backend.Tests.Process;

public sealed class PathExecutableLocatorTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "smm-path-locator-" + Guid.NewGuid().ToString("N"));

    public PathExecutableLocatorTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private PathExecutableLocator Locator(string? path) => new(FileSystem.Shared, () => path);

    private string Write(string name, UnixFileMode mode)
    {
        var file = Path.Combine(_folder, name);
        File.WriteAllText(file, "#!/bin/sh\n");
        File.SetUnixFileMode(file, mode);
        return file;
    }

    [Fact]
    public void Find_ReturnsTheFirstExecutableOnThePath()
    {
        var file = Write("heroic", UnixFileMode.UserRead | UnixFileMode.UserExecute);

        Locator($"/nonexistent:{_folder}").Find("heroic")!.Value.ToString().Should().Be(file);
    }

    [Fact]
    public void Find_SkipsAFileThatIsNotExecutable()
        => (Write("lutris", UnixFileMode.UserRead | UnixFileMode.UserWrite), Locator(_folder).Find("lutris")).Item2.Should().BeNull();

    [Fact]
    public void Find_SkipsRelativeFoldersAndAnEmptyPath()
    {
        Write("bwrap", UnixFileMode.UserRead | UnixFileMode.UserExecute);

        Locator("relative/folder").Find("bwrap").Should().BeNull();
        Locator(null).Find("bwrap").Should().BeNull();
    }
}
