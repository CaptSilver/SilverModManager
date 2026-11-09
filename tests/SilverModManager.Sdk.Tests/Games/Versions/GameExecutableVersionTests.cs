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

    private AbsolutePath ExecutableWithStringTable(uint fileVersionMs, uint fileVersionLs, string productVersion)
    {
        var path = Path.Combine(_folder, "game.exe");
        File.WriteAllBytes(path, NativeExecutable.WithVersionResource(fileVersionMs, fileVersionLs, productVersion));
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

    [Fact]
    public void TheStringTableVersionWinsOverAPlaceholderFixedVersion()
    {
        // SkyrimVR.exe: 1.0.0.0 in the numeric block, 1.4.15.0 in the string table.
        var version = GameExecutableVersion.Read(ExecutableWithStringTable(0x0001_0000, 0, "1.4.15.0"));

        version.HasValue.Should().BeTrue();
        version.Value.Should().Be(new Version(1, 4, 15, 0));
    }

    [Fact]
    public void ACommaSeparatedStringTableVersionIsRead()
    {
        var version = GameExecutableVersion.Read(ExecutableWithStringTable(0x0001_0000, 0, "1, 6, 1170, 0"));

        version.HasValue.Should().BeTrue();
        version.Value.Should().Be(new Version(1, 6, 1170, 0));
    }

    [Fact]
    public void AnUnparsableStringTableVersionFallsBackToTheFixedVersion()
    {
        var version = GameExecutableVersion.Read(ExecutableWithStringTable(0x0001_0004, 0x000F_0000, "final build"));

        version.HasValue.Should().BeTrue();
        version.Value.Should().Be(new Version(1, 4, 15, 0));
    }

    [Fact]
    public void AnAllZeroesStringTableVersionFallsBackToTheFixedVersion()
    {
        var version = GameExecutableVersion.Read(ExecutableWithStringTable(0x0001_0004, 0x000F_0000, "0.0.0.0"));

        version.Value.Should().Be(new Version(1, 4, 15, 0));
    }

    [Theory]
    [InlineData("1.4.15.0", 1, 4, 15, 0)]
    [InlineData("1, 4, 15, 0", 1, 4, 15, 0)]
    [InlineData("1,4,15,0", 1, 4, 15, 0)]
    [InlineData(" 1.6.1170.0 ", 1, 6, 1170, 0)]
    [InlineData("1, 6, 1170, 0 (retail)", 1, 6, 1170, 0)]
    [InlineData("1.6.1170.0-beta", 1, 6, 1170, 0)]
    public void ParseProductVersion_ReadsFourNumbersInEitherNotation(string text, int major, int minor, int build, int revision)
        => GameExecutableVersion.ParseProductVersion(text).Should().Be(new Version(major, minor, build, revision));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("final build")]
    [InlineData("1.4")]
    [InlineData("1.4.15")]
    public void ParseProductVersion_RejectsAnythingWithoutFourNumbers(string? text)
        => GameExecutableVersion.ParseProductVersion(text).Should().BeNull();
}
