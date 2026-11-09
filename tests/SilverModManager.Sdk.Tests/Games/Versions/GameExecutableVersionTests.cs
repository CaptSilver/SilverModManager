using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Sdk.Games.Versions;

namespace SilverModManager.Sdk.Tests.Games.Versions;

public class GameExecutableVersionTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("game-exe-version").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private AbsolutePath Executable(uint fileVersionMs, uint fileVersionLs)
    {
        var path = Path.Combine(_folder, "game.exe");
        File.WriteAllBytes(path, NativeExecutable.WithFileVersion(fileVersionMs, fileVersionLs));
        return FileSystem.Shared.FromUnsanitizedFullPath(path);
    }

    [Fact]
    public void ANativeExecutableReadsTheVersionOfItsFixedFileInfo()
    {
        var version = GameExecutableVersion.Read(Executable(0x0001_0004, 0x000F_0000));

        version.HasValue.Should().BeTrue();
        version.Value.Should().Be(new Version(1, 4, 15, 0));
    }

    [Fact]
    public void AnExecutableWhoseVersionIsAllZeroesHasNoVersion()
    {
        GameExecutableVersion.Read(Executable(0, 0)).HasValue.Should().BeFalse("0.0.0.0 is what an unset version reads as, not a version");
    }

    [Fact]
    public void AFileWithNoVersionDataHasNoVersion()
    {
        var path = Path.Combine(_folder, "plain.exe");
        File.WriteAllBytes(path, new byte[256]);

        GameExecutableVersion.Read(FileSystem.Shared.FromUnsanitizedFullPath(path)).HasValue.Should().BeFalse();
    }

    [Fact]
    public void AMissingFileHasNoVersion()
    {
        GameExecutableVersion.Read(FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_folder, "gone.exe"))).HasValue.Should().BeFalse();
    }

    [Fact]
    public void AManagedAssemblyKeepsTheProductVersionTheResourceReaderGives()
    {
        var assembly = FileSystem.Shared.FromUnsanitizedFullPath(typeof(GameExecutableVersionTests).Assembly.Location);

        var version = GameExecutableVersion.Read(assembly);

        version.HasValue.Should().BeTrue();
        version.Value.Should().Be(assembly.FileInfo.GetFileVersionInfo().ProductVersion);
    }
}
