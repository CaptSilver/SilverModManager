using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NexusMods.Platform.Proton.ToolRunner;
using NexusMods.Sdk;
using NSubstitute;
using NexusMods.Sdk.Games;

namespace NexusMods.Platform.Proton.Tests;

public class ProtonServicesTests
{
    [Fact]
    public void AddProton_PassesValidateOnBuild()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(new InMemoryFileSystem(OSInformation.FakeUnix));
        services.AddSingleton(Substitute.For<IProcessRunner>());
        services.AddProton();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        provider.GetRequiredService<IProtonResolver>().Should().BeOfType<ProtonResolver>();
        provider.GetRequiredService<IPrefixResolver>().Should().BeOfType<PrefixResolver>();
        provider.GetRequiredService<IToolRunner>().Should().BeOfType<ToolRunner.ToolRunner>();
        provider.GetServices<IToolRunStrategy>().Select(s => s.Kind).Should().BeEquivalentTo(
        [
            CompatibilityLayerKind.SteamProton,
            CompatibilityLayerKind.Heroic,
            CompatibilityLayerKind.HeroicFlatpak,
            CompatibilityLayerKind.PlainWine,
        ]);
    }
}
