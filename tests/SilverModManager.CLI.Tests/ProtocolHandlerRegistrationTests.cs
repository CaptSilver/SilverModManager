using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.CLI.Types;
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
    public void ManagedGamesIsRegisteredOnceForTheHandlers()
    {
        new ServiceCollection().AddCLI().Count(descriptor => descriptor.ServiceType == typeof(ManagedGames)).Should().Be(1);
    }
}
