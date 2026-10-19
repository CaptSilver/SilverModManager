using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.DependencyInjection;

namespace NexusMods.Abstractions.GuidedInstallers.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection container)
    {
        container
            .AddLogging(builder => builder.AddXUnit());
    }
}

