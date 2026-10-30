using FluentAssertions;

namespace SilverModManager.Sdk.Tests;

/// <summary>
/// Every Error-level log line opens the crash dialog. A download that gave up with a plain reason (the host kept
/// dropping the connection) is shown on its own row, so the jobs that log download failures must pick the level with
/// <c>LogFailure</c> instead of logging Error unconditionally.
/// </summary>
public class DownloadFailureLoggingGuardTests
{
    public static TheoryData<string> DownloadFailureLoggers => new()
    {
        "src/SilverModManager.Networking.NexusWebApi/NexusModsDownloadJob.cs",
        "src/SilverModManager.Collections/DownloadCollectionJob.cs",
        "src/SilverModManager.Collections/InstallCollectionJob.cs",
    };

    [Theory]
    [MemberData(nameof(DownloadFailureLoggers))]
    public void TheJobLogsItsFailureThroughLogFailure(string file)
    {
        var source = File.ReadAllText(Path.Combine(RepoScanGuard.FindRepoRoot(), file));

        source.Should().Contain("Logger.LogFailure(e,");
        source.Should().NotContain("Logger.LogError(e,");
    }
}
