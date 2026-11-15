using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

public class StatusFailureLogTests
{
    [Fact]
    public void Failed_RepeatedFailures_ErrorOnlyForTheFirst()
    {
        var log = new StatusFailureLog();

        log.Failed().Should().Be(LogLevel.Error);
        log.Failed().Should().Be(LogLevel.Debug);
        log.Failed().Should().Be(LogLevel.Debug);
    }

    [Fact]
    public void Failed_AfterARecovery_ErrorsAgain()
    {
        var log = new StatusFailureLog();
        log.Failed();
        log.Failed();

        log.Recovered();

        log.Failed().Should().Be(LogLevel.Error);
        log.Failed().Should().Be(LogLevel.Debug);
    }

    [Fact]
    public void Recovered_WithoutAFailure_ChangesNothing()
    {
        var log = new StatusFailureLog();

        log.Recovered();

        log.Failed().Should().Be(LogLevel.Error);
    }
}
