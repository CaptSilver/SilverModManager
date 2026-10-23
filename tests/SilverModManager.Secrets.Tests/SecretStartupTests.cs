using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Secrets;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Secrets.Migration;
using NSubstitute;
using NexusMods.Paths;
using NexusMods.Sdk;
using Xunit;

namespace NexusMods.Secrets.Tests;

public class SecretStartupTests
{
    private ServiceProvider Build(Action<IServiceCollection>? configure = null, bool compactAfterMove = true)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IEnvironmentReader>(new FakeEnvironment())
            .AddSecrets(new SecretStoreOptions { Mode = SecretStoreMode.NonInteractive, Backend = SecretBackendChoice.Memory, CompactAfterMove = compactAfterMove });
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Priming_describes_the_store_runs_every_step_and_primes_every_primable_without_prompting()
    {
        var step1 = new RecordingStep("one");
        var step2 = new RecordingStep("two");
        var primable = new RecordingPrimable();
        await using var provider = Build(s => s
            .AddSingleton<ISecretMigrationStep>(step1)
            .AddSingleton<ISecretMigrationStep>(step2)
            .AddSingleton<ISecretPrimable>(primable));

        await SecretStartup.PrimeAsync(provider, CancellationToken.None);

        step1.Runs.Should().Be(1);
        step2.Runs.Should().Be(1);
        primable.Primed.Should().Be(1);
    }

    [Fact]
    public async Task Steps_get_the_persistent_store_not_the_overlay()
    {
        var step = new RecordingStep("one");
        await using var provider = Build(s => s.AddSingleton<ISecretMigrationStep>(step));

        await SecretStartup.PrimeAsync(provider, CancellationToken.None);

        step.Store.Should().BeAssignableTo<IPersistentSecretStore>();
    }

    [Fact]
    public async Task Priming_never_unlocks_the_store_even_in_interactive_mode()
    {
        var scratch = Directory.CreateTempSubdirectory("secret-startup-").FullName;
        try
        {
            var shared = FileSystem.Shared;
            var fileSystem = shared.CreateOverlayFileSystem(
                new Dictionary<AbsolutePath, AbsolutePath>(),
                new Dictionary<KnownPath, AbsolutePath> { [KnownPath.XDG_DATA_HOME] = shared.FromUnsanitizedFullPath(scratch) });
            var service = new InMemorySecretStore();
            await using var provider = new ServiceCollection()
                .AddLogging()
                .AddSingleton(fileSystem)
                .AddSingleton<IEnvironmentReader>(new FakeEnvironment())
                .AddSingleton<ISecretServiceStore>(new FakeServiceStore(service))
                .AddSecrets(new SecretStoreOptions { Mode = SecretStoreMode.Interactive, Backend = SecretBackendChoice.Auto })
                .BuildServiceProvider();

            await SecretStartup.PrimeAsync(provider, CancellationToken.None);

            service.Calls.Should().Contain("Describe").And.NotContain("Unlock");
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public async Task A_step_that_throws_is_logged_and_the_rest_still_run()
    {
        var after = new RecordingStep("after");
        var primable = new RecordingPrimable();
        await using var provider = Build(s => s
            .AddSingleton<ISecretMigrationStep>(new ThrowingStep())
            .AddSingleton<ISecretMigrationStep>(after)
            .AddSingleton<ISecretPrimable>(primable));

        var act = () => SecretStartup.PrimeAsync(provider, CancellationToken.None);

        await act.Should().NotThrowAsync();
        after.Runs.Should().Be(1);
        primable.Primed.Should().Be(1);
    }

    [Fact]
    public async Task A_primable_that_throws_does_not_stop_the_next_one()
    {
        var after = new RecordingPrimable();
        await using var provider = Build(s => s
            .AddSingleton<ISecretPrimable>(new ThrowingPrimable())
            .AddSingleton<ISecretPrimable>(after));

        await SecretStartup.PrimeAsync(provider, CancellationToken.None);

        after.Primed.Should().Be(1);
    }

    [Fact]
    public async Task A_primable_that_never_finishes_is_cut_off_by_the_overall_timeout()
    {
        await using var provider = Build(s => s.AddSingleton<ISecretPrimable>(new HangingPrimable()));

        var started = DateTime.UtcNow;
        await SecretStartup.PrimeAsync(provider, CancellationToken.None, TimeSpan.FromMilliseconds(200));

        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task Priming_without_a_secret_store_registered_does_not_throw()
    {
        await using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();

        var act = () => SecretStartup.PrimeAsync(provider, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task The_runner_reports_whether_anything_moved()
    {
        await using var provider = Build(s => s
            .AddSingleton<ISecretMigrationStep>(new RecordingStep("nothing"))
            .AddSingleton<ISecretMigrationStep>(new RecordingStep("moved", new SecretMigrationOutcome.Moved())));

        var moved = await provider.GetRequiredService<SecretMigrationRunner>().RunAsync(CancellationToken.None);

        moved.Should().BeTrue();
    }

    [Fact]
    public async Task A_deferred_step_is_not_a_move()
    {
        await using var provider = Build(s => s
            .AddSingleton<ISecretMigrationStep>(new RecordingStep("deferred", new SecretMigrationOutcome.Deferred("locked"))));

        (await provider.GetRequiredService<SecretMigrationRunner>().RunAsync(CancellationToken.None)).Should().BeFalse();
    }

    private static Task Prime(ServiceProvider provider, List<Action> scheduled)
        => SecretStartup.PrimeAsync(provider, CancellationToken.None, TimeSpan.FromSeconds(5), scheduled.Add);

    [Fact]
    public async Task A_move_schedules_one_compaction_on_a_background_thread()
    {
        var connection = Substitute.For<IConnection>();
        await using var provider = Build(s => s
            .AddSingleton(connection)
            .AddSingleton<ISecretMigrationStep>(new RecordingStep("moved", new SecretMigrationOutcome.Moved()))
            .AddSingleton<ISecretMigrationStep>(new RecordingStep("moved-too", new SecretMigrationOutcome.Moved())));
        var scheduled = new List<Action>();

        await Prime(provider, scheduled);

        scheduled.Should().ContainSingle("however many steps moved, the database is compacted once");
        await connection.DidNotReceiveWithAnyArgs().FlushAndCompact(default);
        scheduled.Single()();
        await connection.Received(1).FlushAndCompact(false);
    }

    [Fact]
    public async Task Nothing_moved_means_no_compaction()
    {
        var connection = Substitute.For<IConnection>();
        await using var provider = Build(s => s
            .AddSingleton(connection)
            .AddSingleton<ISecretMigrationStep>(new RecordingStep("empty"))
            .AddSingleton<ISecretMigrationStep>(new RecordingStep("waiting", new SecretMigrationOutcome.Deferred("locked"))));
        var scheduled = new List<Action>();

        await Prime(provider, scheduled);

        scheduled.Should().BeEmpty();
    }

    [Fact]
    public async Task A_headless_run_never_compacts_even_after_a_move()
    {
        var connection = Substitute.For<IConnection>();
        await using var provider = Build(s => s
            .AddSingleton(connection)
            .AddSingleton<ISecretMigrationStep>(new RecordingStep("moved", new SecretMigrationOutcome.Moved())), compactAfterMove: false);
        var scheduled = new List<Action>();

        await Prime(provider, scheduled);

        scheduled.Should().BeEmpty();
        connection.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_move_without_a_connection_in_the_container_does_not_throw()
    {
        await using var provider = Build(s => s.AddSingleton<ISecretMigrationStep>(new RecordingStep("moved", new SecretMigrationOutcome.Moved())));
        var scheduled = new List<Action>();

        var act = () => Prime(provider, scheduled);

        await act.Should().NotThrowAsync();
        scheduled.Should().BeEmpty();
    }

    [Fact]
    public async Task A_compaction_that_fails_is_logged_and_not_thrown_into_the_background_thread()
    {
        var connection = Substitute.For<IConnection>();
        connection.When(c => c.FlushAndCompact(Arg.Any<bool>())).Do(_ => throw new InvalidOperationException("closed"));
        await using var provider = Build(s => s
            .AddSingleton(connection)
            .AddSingleton<ISecretMigrationStep>(new RecordingStep("moved", new SecretMigrationOutcome.Moved())));
        var scheduled = new List<Action>();
        await Prime(provider, scheduled);

        var act = () => scheduled.Single()();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task The_default_scheduler_runs_the_compaction_without_the_caller_waiting_for_it()
    {
        var compacting = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var connection = Substitute.For<IConnection>();
        connection.When(c => c.FlushAndCompact(Arg.Any<bool>())).Do(_ =>
        {
            compacting.SetResult();
            release.Task.Wait(TimeSpan.FromSeconds(10));
        });
        await using var provider = Build(s => s
            .AddSingleton(connection)
            .AddSingleton<ISecretMigrationStep>(new RecordingStep("moved", new SecretMigrationOutcome.Moved())));

        await SecretStartup.PrimeAsync(provider, CancellationToken.None);

        await compacting.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        release.SetResult();
    }

    private sealed class RecordingStep(string name, SecretMigrationOutcome? outcome = null) : ISecretMigrationStep
    {
        public int Runs { get; private set; }
        public ISecretStore? Store { get; private set; }
        public string Name => name;

        public Task<SecretMigrationOutcome> RunAsync(ISecretStore store, CancellationToken cancellationToken)
        {
            Runs++;
            Store = store;
            return Task.FromResult(outcome ?? new SecretMigrationOutcome.NothingToMove());
        }
    }

    private sealed class ThrowingStep : ISecretMigrationStep
    {
        public string Name => "throws";
        public Task<SecretMigrationOutcome> RunAsync(ISecretStore store, CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
    }

    private sealed class RecordingPrimable : ISecretPrimable
    {
        public int Primed { get; private set; }

        public ValueTask PrimeAsync(CancellationToken cancellationToken)
        {
            Primed++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingPrimable : ISecretPrimable
    {
        public ValueTask PrimeAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
    }

    private sealed class HangingPrimable : ISecretPrimable
    {
        public async ValueTask PrimeAsync(CancellationToken cancellationToken) => await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
