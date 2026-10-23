using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.App;

namespace SilverModManager.UI.Tests;

public class LauncherShimRegistrationTests
{
    // Internal to Platform.Proton; looked up by name so the app's registration can be checked from here.
    private static readonly Type Refresher = typeof(SilverModManager.Platform.Proton.Services).Assembly
        .GetType("SilverModManager.Platform.Proton.Steam.LauncherShimRefresher", throwOnError: true)!;

    private static IServiceCollection AppServices(bool showUi, bool designer = false)
        => new ServiceCollection().AddLogging().AddApp(startupMode: new StartupMode
        {
            RunAsMain = true,
            ShowUI = showUi,
            ExecuteCli = false,
            IsAvaloniaDesigner = designer,
        });

    [Fact]
    public void Test_GuiProcessRegistersARefresherItCanBuild()
    {
        var services = AppServices(showUi: true);

        services.Should().ContainSingle(d => d.ImplementationType == Refresher);

        // Checked against the descriptors rather than resolved: resolving the interop builds the real database.
        var parameters = Refresher.GetConstructors().Single().GetParameters().Select(p => p.ParameterType);
        parameters.Should().OnlyContain(type => IsRegistered(services, type));
    }

    private static bool IsRegistered(IServiceCollection services, Type type)
        => services.Any(d => d.ServiceType == type
                             || (type.IsGenericType && d.ServiceType == type.GetGenericTypeDefinition()));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Test_WindowlessProcessesDoNotRegisterTheRefresher(bool showUi, bool designer)
    {
        AppServices(showUi, designer).Should().NotContain(d => d.ImplementationType == Refresher);
    }
}
