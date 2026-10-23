using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Secrets;
using NexusMods.Paths;
using NexusMods.Sdk;
using Xunit;

namespace NexusMods.Secrets.Tests;

public class SecretStartupTests
{
    private ServiceProvider Build(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IEnvironmentReader>(new FakeEnvironment())
            .AddSecrets(new SecretStoreOptions { Mode = SecretStoreMode.NonInteractive, Backend = SecretBackendChoice.Memory });
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
