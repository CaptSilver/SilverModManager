using Xunit;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Secrets.Chain;
using SilverModManager.Secrets.Migration;

namespace SilverModManager.Secrets.Tests.Migration;

public class SecretMigrationRunnerTests
{
    private readonly InMemorySecretStore _store = new();
    private readonly LineLogger _log = new();

    private SecretMigrationRunner Runner(params ISecretMigrationStep[] steps)
        => new(new PersistentSecretStore(_store), steps, _log);

    [Fact]
    public async Task StepsRunInRegistrationOrderAgainstTheStoreTheRunnerWasGiven()
    {
        var order = new List<string>();
        var stores = new List<ISecretStore>();
        var runner = Runner(new Step("first", order, stores), new Step("second", order, stores), new Step("third", order, stores));

        await runner.RunAsync(CancellationToken.None);

        order.Should().Equal("first", "second", "third");
        stores.Should().OnlyContain(s => s is IPersistentSecretStore);
    }

    [Fact]
    public async Task EachStepGetsOneLineNamingItAndWhatItDid()
    {
        var runner = Runner(
            new Step("moved-one", outcome: new SecretMigrationOutcome.Moved()),
            new Step("empty-one", outcome: new SecretMigrationOutcome.NothingToMove()),
            new Step("waiting-one", outcome: new SecretMigrationOutcome.Deferred("the keyring is locked")));

        await runner.RunAsync(CancellationToken.None);

        _log.Lines.Should().Equal(
            "Secret migration moved-one: Moved",
            "Secret migration empty-one: NothingToMove",
            "Secret migration waiting-one: Deferred(the keyring is locked)");
    }

    [Fact]
    public async Task AStepThatThrowsIsLoggedAsDeferredWithItsMessageAndTheRestStillRun()
    {
        var order = new List<string>();
        var runner = Runner(new ThrowingStep(), new Step("after", order, outcome: new SecretMigrationOutcome.Moved()));

        var moved = await runner.RunAsync(CancellationToken.None);

        moved.Should().BeTrue("a step after the failing one still moved its credential");
        order.Should().Equal("after");
        _log.Lines.Should().Contain("Secret migration throws: Deferred(boom)");
        _log.Lines.Should().Contain("Secret migration after: Moved");
    }

    [Fact]
    public async Task OnlyAMoveCountsAsAMove()
    {
        (await Runner(new Step("a"), new Step("b", outcome: new SecretMigrationOutcome.Deferred("later"))).RunAsync(CancellationToken.None))
            .Should().BeFalse();
        (await Runner(new Step("a"), new Step("b", outcome: new SecretMigrationOutcome.Moved())).RunAsync(CancellationToken.None))
            .Should().BeTrue();
    }

    [Fact]
    public async Task ACancelledRunIsNotSwallowedAsADeferral()
    {
        var runner = Runner(new CancellingStep());

        var act = () => runner.RunAsync(CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class Step(string name, List<string>? order = null, List<ISecretStore>? stores = null, SecretMigrationOutcome? outcome = null) : ISecretMigrationStep
    {
        public string Name => name;

        public Task<SecretMigrationOutcome> RunAsync(ISecretStore store, CancellationToken cancellationToken)
        {
            order?.Add(name);
            stores?.Add(store);
            return Task.FromResult(outcome ?? new SecretMigrationOutcome.NothingToMove());
        }
    }

    private sealed class ThrowingStep : ISecretMigrationStep
    {
        public string Name => "throws";
        public Task<SecretMigrationOutcome> RunAsync(ISecretStore store, CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
    }

    private sealed class CancellingStep : ISecretMigrationStep
    {
        public string Name => "cancels";
        public Task<SecretMigrationOutcome> RunAsync(ISecretStore store, CancellationToken cancellationToken) => throw new OperationCanceledException();
    }

    private sealed class LineLogger : ILogger<SecretMigrationRunner>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }
}
