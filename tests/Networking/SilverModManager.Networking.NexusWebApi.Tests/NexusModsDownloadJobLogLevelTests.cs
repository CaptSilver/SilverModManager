using FluentAssertions;
using Microsoft.Extensions.Logging;
using SilverModManager.Networking.HttpDownloader;
using SilverModManager.Sdk.Errors;
using Xunit;

namespace SilverModManager.Networking.NexusWebApi.Tests;

/// <summary>
/// Every Error-level log line opens a crash dialog. A download that gave up with a plain reason is shown on its row
/// instead, so it must not be logged as an Error.
/// </summary>
public class NexusModsDownloadJobLogLevelTests
{
    [Fact]
    public void AHostThatKeptDroppingTheConnection_IsLoggedAsAWarning()
    {
        var failure = new ConnectionDroppedException("cdn.test", NexusMods.Paths.Size.FromLong(10), DynamicData.Kernel.Optional<NexusMods.Paths.Size>.None, new IOException("unexpected EOF"));

        NexusModsDownloadJob.LogLevelOf(failure).Should().Be(LogLevel.Warning);
    }

    [Fact]
    public void ADownloadWrappedInAnotherExceptionKeepsItsPlainReasonsLevel()
    {
        var failure = new AggregateException(new DownloadStalledException("cdn.test"));

        NexusModsDownloadJob.LogLevelOf(failure).Should().Be(LogLevel.Warning);
    }

    [Fact]
    public void AnUnexpectedFailure_IsLoggedAsAnError()
    {
        NexusModsDownloadJob.LogLevelOf(new InvalidOperationException("bug")).Should().Be(LogLevel.Error);
    }
}
