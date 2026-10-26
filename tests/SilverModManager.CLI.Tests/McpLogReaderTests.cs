using FluentAssertions;
using Microsoft.Extensions.Logging;
using SilverModManager.App.Mcp;

namespace SilverModManager.CLI.Tests;

public sealed class McpLogReaderTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mcp-log-tests").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static readonly DateTime Started = new(2026, 10, 25, 12, 0, 0);

    private string Write(string content)
    {
        var path = Path.Combine(_directory, "main.current.log");
        File.WriteAllText(path, content);
        return path;
    }

    private const string Sample =
        "############ SilverModManager log file - 2026-10-25 12:00:00.0000 ############\n" +
        "00:00:01.100 [INFO] (App) Started\n" +
        "00:00:02.200 [WARN] (Synchronizer) Slow disk\n" +
        "00:00:03.300 [ERROR] (Deployer) Could not hardlink\n" +
        "   at Deployer.Link()\n" +
        "   at Deployer.Run()\n" +
        "00:00:04.400 [DEBUG] (App) Noise\n" +
        "00:10:00.000 [FATAL] (App) Gave up\n";

    [Fact]
    public void ByDefaultOnlyWarningsAndWorseAreKept_AndATraceStaysWithItsEntry()
    {
        var text = LogReader.Read(Write(Sample), LogLevel.Warning, 50, null, Started.AddHours(1));

        text.Should().Contain("Slow disk").And.Contain("Could not hardlink").And.Contain("at Deployer.Link()").And.Contain("Gave up");
        text.Should().NotContain("Started").And.NotContain("Noise");
    }

    [Fact]
    public void AHigherMinimumLevel_DropsTheWarnings()
    {
        var text = LogReader.Read(Write(Sample), LogLevel.Error, 50, null, Started.AddHours(1));

        text.Should().NotContain("Slow disk").And.Contain("Could not hardlink").And.Contain("Gave up");
    }

    [Fact]
    public void TheLimitKeepsTheNewestEntries()
    {
        var text = LogReader.Read(Write(Sample), LogLevel.Warning, 2, null, Started.AddHours(1));

        text.Should().NotContain("Slow disk").And.Contain("Could not hardlink").And.Contain("Gave up");
    }

    [Fact]
    public void SinceKeepsEntriesFromTheLastMinutes_WorkedOutFromTheHeaderAndTheProcessTime()
    {
        // The last entry is ten minutes into the run; now is eleven minutes in, so five minutes back reaches only it.
        var text = LogReader.Read(Write(Sample), LogLevel.Warning, 50, TimeSpan.FromMinutes(5), Started.AddMinutes(11));

        text.Should().Contain("Gave up").And.NotContain("Could not hardlink");
    }

    [Fact]
    public void ASinceFurtherBackThanTheCalendarGoes_KeepsEverything()
    {
        var text = LogReader.Read(Write(Sample), LogLevel.Warning, 50, TimeSpan.FromMinutes(int.MaxValue), Started.AddMinutes(11));

        text.Should().Contain("Slow disk").And.Contain("Gave up");
    }

    [Fact]
    public void ALogWithNothingToReport_SaysSo()
    {
        var text = LogReader.Read(Write("############ SilverModManager log file - 2026-10-25 12:00:00.0000 ############\n00:00:01.100 [INFO] (App) Started\n"), LogLevel.Warning, 50, null, Started);

        text.Should().Contain("No entries");
    }

    [Fact]
    public void AMissingLogFile_SaysWhichFileItLookedFor()
    {
        var path = Path.Combine(_directory, "absent.log");

        LogReader.Read(path, LogLevel.Warning, 50, null, Started).Should().Contain("absent.log").And.Contain("no log");
    }

    [Fact]
    public void ALogAnotherProcessHasOpen_CanStillBeRead()
    {
        var path = Write(Sample);
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        LogReader.Read(path, LogLevel.Warning, 50, null, Started.AddHours(1)).Should().Contain("Slow disk");
    }
}
