using FluentAssertions;
using SilverModManager.App;
using SilverModManager.CrossPlatform;
using NexusMods.Paths;

namespace SilverModManager.CLI.Tests;

public class LogFileSelectionTests
{
    private static readonly InMemoryFileSystem Fs = new(OSInformation.FakeUnix);
    private static readonly LoggingSettings Settings = LoggingSettings.CreateDefault();

    private static (AbsolutePath Current, AbsolutePath Archive) Select(params string[] args)
    {
        var (current, archive) = LogFileSelection.Select(Settings, StartupMode.Parse(args));
        return (current.ToPath(Fs), archive.ToPath(Fs));
    }

    [Fact]
    public void Run_UsesTheRunLogFile()
    {
        var (current, archive) = Select("run", "--", "game.exe");

        current.Should().Be(Settings.RunProcessLogFilePath.ToPath(Fs));
        archive.Should().Be(Settings.RunProcessArchiveFilePath.ToPath(Fs));
    }

    [Fact]
    public void Gui_UsesTheMainLogFile()
    {
        var (current, archive) = Select();

        current.Should().Be(Settings.MainProcessLogFilePath.ToPath(Fs));
        archive.Should().Be(Settings.MainProcessArchiveFilePath.ToPath(Fs));
    }

    [Fact]
    public void HeadlessMain_UsesTheMainLogFile()
    {
        Select("as-main", "list-games").Current.Should().Be(Settings.MainProcessLogFilePath.ToPath(Fs));
    }

    [Theory]
    [InlineData("mcp")]
    [InlineData("mcp --read-only")]
    public void Mcp_UsesItsOwnLogFile(string input)
    {
        var (current, archive) = Select(input.Split(' '));

        current.Should().Be(Settings.McpProcessLogFilePath.ToPath(Fs));
        archive.Should().Be(Settings.McpProcessArchiveFilePath.ToPath(Fs));
        current.Should().NotBe(Settings.SlimProcessLogFilePath.ToPath(Fs));
        current.FileName.Should().Contain(".mcp.");
    }

    [Fact]
    public void SlimClient_UsesTheSlimLogFile()
    {
        var (current, archive) = Select("list-games");

        current.Should().Be(Settings.SlimProcessLogFilePath.ToPath(Fs));
        archive.Should().Be(Settings.SlimProcessArchiveFilePath.ToPath(Fs));
    }
}
