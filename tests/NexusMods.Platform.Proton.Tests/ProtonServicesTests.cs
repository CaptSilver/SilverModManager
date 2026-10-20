using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Protontricks;
using NexusMods.Platform.Proton.ToolRunner;
using NexusMods.Platform.Proton.UserTools;
using NexusMods.Sdk;
using NSubstitute;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Settings;
using NexusMods.Platform.Proton.Support;

namespace NexusMods.Platform.Proton.Tests;

public class ProtonServicesTests
{
    [Fact]
    public void AddProton_PassesValidateOnBuild()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(new InMemoryFileSystem(OSInformation.FakeUnix));
        services.AddSingleton(Substitute.For<IProcessRunner>());
        services.AddSingleton(Substitute.For<IConnection>());
        services.AddSingleton(Substitute.For<IGameRegistry>());
        services.AddSingleton(Substitute.For<ISettingsManager>());
        services.AddProton();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        provider.GetRequiredService<IProtonResolver>().Should().BeOfType<ProtonResolver>();
        provider.GetRequiredService<IPrefixResolver>().Should().BeOfType<PrefixResolver>();
        provider.GetRequiredService<ISupportBundleWriter>().Should().BeOfType<SupportBundleWriter>();
        provider.GetRequiredService<IToolRunner>().Should().BeOfType<ToolRunner.ToolRunner>();
        provider.GetServices<IToolProvider>().Should().ContainSingle().Which.Should().BeOfType<UserToolProvider>();
        provider.GetServices<IToolRunStrategy>().Select(s => s.Kind).Should().BeEquivalentTo(
        [
            CompatibilityLayerKind.SteamProton,
            CompatibilityLayerKind.Heroic,
            CompatibilityLayerKind.HeroicFlatpak,
            CompatibilityLayerKind.PlainWine,
            CompatibilityLayerKind.SteamProton,
        ]);
        provider.GetServices<IToolRunStrategy>().OfType<ILastResortToolRunStrategy>().Should().ContainSingle()
            .Which.Should().BeOfType<ProtontricksStrategy>();
        provider.GetServices<IRuntimeDependency>().Should().ContainSingle().Which.Should().BeOfType<AggregateProtontricksDependency>();
    }
}
