using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk;

namespace SilverModManager.Games.AdvancedInstaller.UI.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services
            .AddDefaultServicesForTesting()
            .AddLogging(builder => builder.AddXUnit())
            .Validate();
    }
}
