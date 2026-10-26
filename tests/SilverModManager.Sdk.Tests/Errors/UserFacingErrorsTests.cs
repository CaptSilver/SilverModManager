using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Sdk.Tests.Errors;

public class UserFacingErrorsTests
{
    private sealed class Refusal(string message, string title = "Refused") : UserFacingException(message)
    {
        public override string Title => title;
    }

    private sealed class RecordingLogger : ILogger
    {
        public LogLevel? Level;
        public Exception? Logged;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Level = logLevel;
            Logged = exception;
        }
    }

    [Fact]
    public void Find_ReturnsNull_ForPlainException()
    {
        UserFacingErrors.Find(new InvalidOperationException("boom")).Should().BeNull();
    }

    [Fact]
    public void Find_ReturnsNull_ForNull()
    {
        UserFacingErrors.Find(null).Should().BeNull();
    }

    [Fact]
    public void Find_ReturnsTheRefusal_ForADirectRefusal()
    {
        var refusal = new Refusal("no");
        UserFacingErrors.Find(refusal).Should().BeSameAs(refusal);
    }

    [Fact]
    public void Find_ReturnsRefusal_ForNestedAggregateOfRefusals()
    {
        var inner = new Refusal("inner");
        var ex = new AggregateException(new AggregateException(inner));

        var found = UserFacingErrors.Find(ex);

        found.Should().NotBeNull();
        found!.Message.Should().Be("inner");
    }

    [Fact]
    public void Find_ReturnsNull_ForAggregateWithAPlainLeaf()
    {
        var ex = new AggregateException(new Refusal("no"), new InvalidOperationException("bug"));
        UserFacingErrors.Find(ex).Should().BeNull();
    }

    [Fact]
    public void Find_ReturnsNull_ForNestedAggregateWithAPlainLeaf()
    {
        var ex = new AggregateException(new AggregateException(new Refusal("a")), new AggregateException(new NullReferenceException()));
        UserFacingErrors.Find(ex).Should().BeNull();
    }

    [Fact]
    public void Find_UnwrapsTargetInvocationException()
    {
        var refusal = new Refusal("no");
        UserFacingErrors.Find(new TargetInvocationException(refusal)).Should().BeSameAs(refusal);
    }

    [Fact]
    public void Find_ReturnsNull_ForTargetInvocationOfPlainException()
    {
        UserFacingErrors.Find(new TargetInvocationException(new InvalidOperationException())).Should().BeNull();
    }

    [Fact]
    public void Find_DoesNotUnwrapTypeInitializationException()
    {
        var ex = new TypeInitializationException("Some.Type", new Refusal("no"));
        UserFacingErrors.Find(ex).Should().BeNull();
    }

    [Fact]
    public void Find_JoinsSeveralLeaves_AndUsesTheFirstTitle()
    {
        var ex = new AggregateException(new Refusal("one", "First title"), new Refusal("two", "Second title"));

        var found = UserFacingErrors.Find(ex);

        found.Should().NotBeNull();
        found!.Title.Should().Be("First title");
        found.Message.Should().Be("one" + Environment.NewLine + "two");
    }

    [Fact]
    public void Find_ReturnsTheRefusalItself_WhenTheSameInstanceIsALeafTwice()
    {
        var refusal = new Refusal("no");
        UserFacingErrors.Find(new AggregateException(refusal, new AggregateException(refusal))).Should().BeSameAs(refusal);
    }

    [Fact]
    public void Find_JoinsEachMessageOnce_WhenLeavesRepeatAMessage()
    {
        var ex = new AggregateException(new Refusal("one"), new Refusal("two"), new Refusal("one"));
        UserFacingErrors.Find(ex)!.Message.Should().Be("one" + Environment.NewLine + "two");
    }

    [Fact]
    public void Find_KeepsTheFirstLeafAction_WhenLeavesAreJoined()
    {
        var first = new Refusal("one") { Action = new OpenHealthCheck(default) };
        var found = UserFacingErrors.Find(new AggregateException(first, new Refusal("two")));

        found!.Action.Should().Be(first.Action);
    }

    [Fact]
    public void LogFailure_LogsWarning_ForARefusal()
    {
        var logger = new RecordingLogger();
        var refusal = new Refusal("no");

        logger.LogFailure(refusal, "Apply failed");

        logger.Level.Should().Be(LogLevel.Warning);
        logger.Logged.Should().BeSameAs(refusal);
    }

    [Fact]
    public void LogFailure_LogsError_ForACrash()
    {
        var logger = new RecordingLogger();
        var ex = new InvalidOperationException("bug");

        logger.LogFailure(ex, "Apply failed");

        logger.Level.Should().Be(LogLevel.Error);
        logger.Logged.Should().BeSameAs(ex);
    }
}
