using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;

namespace SilverModManager.Deployment.Links.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection services) => services.AddFileSystem();
}
