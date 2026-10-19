using FluentAssertions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Backend.Tests.Games;

public class ProtonPrefixTests
{
    [Fact]
    public void IsReady_WithSystemReg_IsTrue()
    {
        var (fileSystem, prefix) = CreatePrefixDirectory();
        fileSystem.WriteAllText(prefix.Combine("system.reg"), "");

        ProtonPrefix.IsReady(prefix).Should().BeTrue();
    }

    [Fact]
    public void IsReady_EmptyDirectory_IsFalse()
    {
        var (_, prefix) = CreatePrefixDirectory();

        ProtonPrefix.IsReady(prefix).Should().BeFalse("an empty pfx has no registry yet");
    }

    [Fact]
    public void IsReady_MissingDirectory_IsFalse()
    {
        var fileSystem = new InMemoryFileSystem(OSInformation.FakeUnix);

        ProtonPrefix.IsReady(fileSystem.FromUnsanitizedFullPath("/library/compatdata/1/pfx")).Should().BeFalse();
    }

    private static (InMemoryFileSystem, AbsolutePath) CreatePrefixDirectory()
    {
        var fileSystem = new InMemoryFileSystem(OSInformation.FakeUnix);
        var prefix = fileSystem.FromUnsanitizedFullPath("/library/compatdata/1/pfx");
        fileSystem.CreateDirectory(prefix);
        return (fileSystem, prefix);
    }
}
