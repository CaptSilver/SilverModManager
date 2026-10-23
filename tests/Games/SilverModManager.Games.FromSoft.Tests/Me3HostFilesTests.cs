using FluentAssertions;
using SilverModManager.Games.FromSoft.Me3;
using NexusMods.Paths;

namespace SilverModManager.Games.FromSoft.Tests;

public class Me3HostFilesTests
{
    private static AbsolutePath NewBinary(UnixFileMode mode)
    {
        var path = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory) / $"me3-host-{Guid.NewGuid():N}";
        File.WriteAllBytes(path.ToString(), [0]);
        File.SetUnixFileMode(path.ToString(), mode);
        return path;
    }

    private const UnixFileMode Plain = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    [Fact]
    public void ADeployedBinaryWithoutTheExecuteBitIsNotExecutable()
    {
        var path = NewBinary(Plain);
        try { Me3HostFiles.IsExecutable(path).Should().BeFalse(); }
        finally { path.Delete(); }
    }

    [Fact]
    public void EnsureExecutableSetsMode755()
    {
        var path = NewBinary(Plain);
        try
        {
            Me3HostFiles.EnsureExecutable(path);

            Me3HostFiles.IsExecutable(path).Should().BeTrue();
            File.GetUnixFileMode(path.ToString()).Should().Be((UnixFileMode)0b111_101_101);
        }
        finally { path.Delete(); }
    }

    [Fact]
    public void EnsureExecutableOnAnExecutableBinaryChangesNothing()
    {
        var path = NewBinary((UnixFileMode)0b111_101_101);
        try
        {
            Me3HostFiles.EnsureExecutable(path);
            Me3HostFiles.IsExecutable(path).Should().BeTrue();
        }
        finally { path.Delete(); }
    }

    [Fact]
    public void EnsureExecutableOnAMissingFileThrows()
    {
        var path = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory) / $"me3-missing-{Guid.NewGuid():N}";

        var act = () => Me3HostFiles.EnsureExecutable(path);

        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void IsExecutableIsFalseForAMissingFile()
    {
        var path = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory) / $"me3-missing-{Guid.NewGuid():N}";

        Me3HostFiles.IsExecutable(path).Should().BeFalse();
    }
}
