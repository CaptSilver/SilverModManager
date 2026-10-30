using System.Reactive.Subjects;
using FluentAssertions;
using SilverModManager.DataModel.Extensions;

namespace SilverModManager.DataModel.Tests;

public class ObservableExtensionsTests
{
    [Fact]
    public void NotNull_EmitsOnlyTheValuesThatAreNotNull()
    {
        var source = new Subject<string?>();
        var seen = new List<string>();
        using var subscription = source.NotNull().Subscribe(seen.Add);

        source.OnNext("a");
        source.OnNext(null);
        source.OnNext("b");

        seen.Should().Equal("a", "b");
    }

    [Fact]
    public void NotNull_PassesCompletionAndErrorsThrough()
    {
        var completed = new Subject<string?>();
        var isCompleted = false;
        using var completedSubscription = completed.NotNull().Subscribe(_ => { }, () => isCompleted = true);
        completed.OnCompleted();
        isCompleted.Should().BeTrue();

        var failing = new Subject<string?>();
        Exception? error = null;
        using var failingSubscription = failing.NotNull().Subscribe(_ => { }, e => error = e);
        failing.OnError(new InvalidOperationException("boom"));
        error.Should().BeOfType<InvalidOperationException>();
    }
}
