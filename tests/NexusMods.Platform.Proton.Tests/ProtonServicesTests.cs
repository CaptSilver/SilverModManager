using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Platform.Proton.Tests;

public class ProtonServicesTests
{
    [Fact]
    public void AddProton_PassesValidateOnBuild()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(new InMemoryFileSystem(OSInformation.FakeUnix));
        services.AddProton();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        provider.GetRequiredService<IProtonResolver>().Should().BeOfType<ProtonResolver>();
    }
}
