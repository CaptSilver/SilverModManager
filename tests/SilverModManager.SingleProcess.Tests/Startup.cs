using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SilverModManager.SingleProcess.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection container)
    {
        container.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));
    }
}
