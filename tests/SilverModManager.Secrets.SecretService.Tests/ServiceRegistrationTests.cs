using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Secrets;
using NexusMods.Sdk;
using Xunit;

namespace NexusMods.Secrets.SecretService.Tests;

public class ServiceRegistrationTests
{
    private static ServiceCollection WithOptions(SecretStoreOptions options)
    {
        var services = new ServiceCollection();
        services.AddSingleton(options);
        return services;
    }

    [Fact]
    public void Memory_backend_leaves_no_Secret_Service_descriptor_behind()
    {
        var services = WithOptions(new SecretStoreOptions { Mode = SecretStoreMode.NonInteractive, Backend = SecretBackendChoice.Memory });

        services.AddSecretServiceBackend();

        services.Should().NotContain(d => d.ServiceType == typeof(ISecretServiceStore));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(SecretBackendChoice.Auto)]
    [InlineData(SecretBackendChoice.FileOnly)]
    public void Other_backends_register_the_Secret_Service_store(SecretBackendChoice? backend)
    {
        var services = WithOptions(new SecretStoreOptions { Mode = SecretStoreMode.NonInteractive, Backend = backend });

        services.AddSecretServiceBackend();

        services.Should().ContainSingle(d => d.ServiceType == typeof(ISecretServiceStore) && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void The_registered_store_resolves_with_the_environment_and_options_from_the_container()
    {
        var services = WithOptions(new SecretStoreOptions { Mode = SecretStoreMode.Interactive });
        services.AddLogging();
        services.AddSingleton<IEnvironmentReader, ProcessEnvironmentReader>();
        services.AddSecretServiceBackend();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISecretServiceStore>().Should().NotBeNull();
        provider.GetRequiredService<ISecretServiceStore>().Should().BeSameAs(provider.GetRequiredService<ISecretServiceStore>());
    }

    [Fact]
    public void Registering_without_secret_store_options_fails_loudly()
    {
        var act = () => new ServiceCollection().AddSecretServiceBackend();

        act.Should().Throw<InvalidOperationException>().WithMessage("*AddSecrets*");
    }
}
