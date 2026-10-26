using System.Reactive.Subjects;
using FluentAssertions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Logging;
using SilverModManager.App.UI.Windows;

namespace SilverModManager.UI.Tests.Windows;

public class UnhandledExceptionReporterTests
{
    private static List<(string Title, string Description, string? Details)> Report(Exception? exception)
    {
        var shown = new List<(string, string, string?)>();
        var errors = new Subject<LogMessage>();
        using var _ = UnhandledExceptionReporter.Connect(errors, (t, d, m) => shown.Add((t, d, m)));

        errors.OnNext(new LogMessage(exception, "job failed"));
        return shown;
    }

    [Fact]
    public void ARefusedApply_IsNotShownAsUnhandled()
    {
        Report(new SynchronizerPreflightException("The prefix does not exist")).Should().BeEmpty();
    }

    [Fact]
    public void ASubclassOfTheRefusal_IsNotShownAsUnhandled()
    {
        Report(new DerivedRefusal()).Should().BeEmpty();
    }

    [Fact]
    public void ARefusalWrappedInAnAggregate_IsNotShownAsUnhandled()
    {
        var wrapped = new AggregateException(new AggregateException(new SynchronizerPreflightException("read-only")));

        Report(wrapped).Should().BeEmpty();
    }

    [Fact]
    public void AGenuineFailure_IsShownAsUnhandled()
    {
        var shown = Report(new InvalidOperationException("boom"));

        var entry = shown.Should().ContainSingle().Subject;
        entry.Title.Should().Be("Unhandled Exception");
        entry.Description.Should().Be("job failed");
        entry.Details.Should().Contain("boom");
    }

    [Fact]
    public void AnErrorWithoutAnException_IsShownAsUnhandled()
    {
        Report(null).Should().ContainSingle().Which.Details.Should().BeNull();
    }

    [Fact]
    public void ARefusalNextToAGenuineFailure_IsShownAsUnhandled()
    {
        var mixed = new AggregateException(new SynchronizerPreflightException("read-only"), new InvalidOperationException("boom"));

        Report(mixed).Should().ContainSingle();
    }

    private class DerivedRefusal() : SynchronizerPreflightException("refused");
}
