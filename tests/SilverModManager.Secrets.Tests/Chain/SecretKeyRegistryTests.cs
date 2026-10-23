using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Secrets;
using Xunit;

namespace SilverModManager.Secrets.Tests.Chain;

public class SecretKeyRegistryTests
{
    [Fact]
    public void Keys_from_every_registration_are_listed()
    {
        var services = new ServiceCollection()
            .AddSecretKeys(new SecretKey("nexus.api-key"))
            .AddSecretKeys(new SecretKey("gog.oauth"), new SecretKey("github.token"));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISecretKeyRegistry>().Keys.Should().BeEquivalentTo(
            [new SecretKey("nexus.api-key"), new SecretKey("gog.oauth"), new SecretKey("github.token")]);
    }

    [Fact]
    public void Two_keys_that_share_an_environment_variable_are_rejected_naming_both()
    {
        var registry = () => new SecretKeyRegistry([new SecretKey("a.b-c"), new SecretKey("a.b.c")]);

        registry.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("a.b-c").And.Contain("a.b.c");
    }

    [Fact]
    public void Registering_the_same_key_twice_is_not_a_clash()
    {
        var services = new ServiceCollection()
            .AddSecretKeys(new SecretKey("nexus.api-key"))
            .AddSecretKeys(new SecretKey("nexus.api-key"));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISecretKeyRegistry>().Keys.Should().ContainSingle();
    }

    [Fact]
    public void AddSecrets_throws_when_registered_keys_clash()
    {
        var services = new ServiceCollection()
            .AddSecretKeys(new SecretKey("a.b-c"))
            .AddSecretKeys(new SecretKey("a.b.c"));

        var add = () => services.AddSecrets(new SecretStoreOptions { Mode = SecretStoreMode.NonInteractive, Backend = SecretBackendChoice.Memory });

        add.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("a.b-c").And.Contain("a.b.c");
    }
}
