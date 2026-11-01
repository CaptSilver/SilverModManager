using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SilverModManager.CLI.Types;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk.Settings;
using SilverModManager.CLI.Types.IpcHandlers;

namespace SilverModManager.CLI.Tests;

/// <summary>The link handlers the app routes a scheme to.</summary>
public class ProtocolHandlerRegistrationTests
{
    [Fact]
    public void NxmAndRor2mmEachHaveOneHandler()
    {
        var handlers = new ServiceCollection().AddCLI()
            .Where(descriptor => descriptor.ServiceType == typeof(IIpcProtocolHandler))
            .Select(descriptor => descriptor.ImplementationType)
            .ToArray();

        handlers.Should().BeEquivalentTo([typeof(NxmIpcProtocolHandler), typeof(Ror2mmIpcProtocolHandler)]);
    }

    [Fact]
    public void AddCLIAloneRegistersNoRegistrationService()
    {
        new ServiceCollection().AddCLI().Should().NotContain(descriptor => descriptor.ImplementationType == typeof(ProtocolRegistration));
    }

    [Fact]
    public void AddNexusWebApiRegistersNoRegistrationService()
    {
        new ServiceCollection().AddNexusWebApi().Should().NotContain(descriptor =>
            descriptor.ImplementationType == typeof(ProtocolRegistration) || descriptor.ImplementationType != null && descriptor.ImplementationType.Name == "HandlerRegistration");
    }

    [Fact]
    public void AddProtocolRegistrationRunsTheServiceOnce()
    {
        var services = new ServiceCollection().AddProtocolRegistration();

        services.Count(descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(ProtocolRegistration)).Should().Be(1);
    }

    [Theory]
    [InlineData(typeof(Ror2mmHandlerSettings))]
    [InlineData(typeof(ProtocolNoticeSettings))]
    public void TheCliRegistersItsOwnSettings(Type settings)
    {
        new ServiceCollection().AddCLI().Select(descriptor => descriptor.ImplementationInstance)
            .OfType<SettingsRegistration>().Should().Contain(registration => registration.ObjectType == settings);
    }

    [Fact]
    public void ManagedGamesIsRegisteredOnceForTheHandlers()
    {
        new ServiceCollection().AddCLI().Count(descriptor => descriptor.ServiceType == typeof(ManagedGames)).Should().Be(1);
    }
}
