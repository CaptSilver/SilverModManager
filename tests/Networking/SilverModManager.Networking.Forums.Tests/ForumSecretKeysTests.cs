using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Secrets;

namespace SilverModManager.Networking.Forums.Tests;

public class ForumSecretKeysTests
{
    [Fact]
    public void KeysMapToTheirOwnEnvironmentVariables()
    {
        ForumSecretKeys.LoversLab.EnvironmentVariable.Should().Be("SILVERMM_SECRET_LOVERSLAB_OAUTH");
        ForumSecretKeys.VectorPlexus.EnvironmentVariable.Should().Be("SILVERMM_SECRET_VECTORPLEXUS_OAUTH");
    }

    [Fact]
    public void AllListsBothSites()
    {
        ForumSecretKeys.All.Should().BeEquivalentTo([ForumSecretKeys.LoversLab, ForumSecretKeys.VectorPlexus]);
    }

    [Fact]
    public void KeysRegisterBesideTheOtherCredentialKeysWithoutAClash()
    {
        var services = new ServiceCollection()
            .AddSecretKeys(new SecretKey("nexus.api-key"), new SecretKey("nexus.oauth"))
            .AddSecretKeys(new SecretKey("gog.oauth"), new SecretKey("github.token"))
            .AddSecretKeys(ForumSecretKeys.All);
        using var provider = services.BuildServiceProvider();

        var build = () => provider.GetRequiredService<ISecretKeyRegistry>();

        build.Should().NotThrow().Which.Keys.Should().Contain(ForumSecretKeys.All);
    }

    [Theory]
    [InlineData(ForumSite.LoversLab, "loverslab.oauth")]
    [InlineData(ForumSite.VectorPlexus, "vectorplexus.oauth")]
    public void EachSiteHasItsKey(ForumSite site, string name)
    {
        site.SecretKey().Name.Should().Be(name);
    }

    [Theory]
    [InlineData(ForumSite.LoversLab, "LoversLab")]
    [InlineData(ForumSite.VectorPlexus, "VectorPlexus")]
    public void EachSiteHasADisplayName(ForumSite site, string name)
    {
        site.DisplayName().Should().Be(name);
    }

    [Fact]
    public void AddForumsRegistersTheKeysAndTheSettings()
    {
        var services = new ServiceCollection().AddForums();

        services.RegisteredSecretKeys().Should().Contain(ForumSecretKeys.All);
        services.Should().Contain(d => d.ImplementationInstance is SilverModManager.Sdk.Settings.SettingsRegistration
                                       && ((SilverModManager.Sdk.Settings.SettingsRegistration)d.ImplementationInstance).ObjectType == typeof(ForumAuthSettings));
    }
}
