using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Protontricks;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Platform.Proton.ToolRunner;
using SilverModManager.Platform.Proton.UserTools;
using SilverModManager.Platform.Proton.Verbs;
using SilverModManager.Sdk;
using NSubstitute;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Settings;
using SilverModManager.Platform.Proton.PrefixSetup;
using SilverModManager.Platform.Proton.Support;

namespace SilverModManager.Platform.Proton.Tests;

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
        services.AddSingleton(Substitute.For<IDeploymentPlanStore>());
        services.AddSingleton(Substitute.For<IDeploymentBackendSelector>());
        services.AddSingleton(Substitute.For<ISynchronizerService>());
        services.AddSingleton(Substitute.For<ILooseStagingStore>());
        services.AddSingleton<DeploymentLaunchRoots>();
        services.AddSingleton(Substitute.For<IGameLaunchSession>());
        services.AddSingleton(Substitute.For<ISettingsManager>());
        services.AddSingleton(Substitute.For<IToolManager>());
        services.AddSingleton(new HttpClient());
        services.AddLogging();
        services.AddProton();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        provider.GetRequiredService<IProtonResolver>().Should().BeOfType<ProtonResolver>();
        provider.GetRequiredService<IPrefixResolver>().Should().BeOfType<PrefixResolver>();
        provider.GetRequiredService<ISupportBundleWriter>().Should().BeOfType<SupportBundleWriter>();
        provider.GetRequiredService<DeploymentCommand>().Should().NotBeNull();
        provider.GetRequiredService<PrefixSetupRunner>().Should().NotBeNull();
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
        provider.GetServices<IDiagnosticEmitter>().Should().ContainSingle().Which.Should().BeOfType<SteamLaunchOptionEmitter>();
        provider.GetServices<IRuntimeDependency>().Should().ContainSingle().Which.Should().BeOfType<AggregateProtontricksDependency>();
    }
}
