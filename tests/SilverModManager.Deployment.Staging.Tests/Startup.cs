using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Backend;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Settings;
using NSubstitute;

namespace SilverModManager.Deployment.Staging.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        var stagingDirectory = FileSystem.Shared
            .GetKnownPath(KnownPath.TempDirectory)
            .Combine($"loose-staging-tests-{Guid.NewGuid():N}");

        services
            .AddFileSystem()
            .AddSettingsManager()
            .AddLogging()
            .AddSingleton(Substitute.For<IFileStore>())
            .AddLooseStagingStore()
            .OverrideSettingsForTests<LooseStagingStoreSettings>(settings => settings with
            {
                Location = new ConfigurablePath(null, stagingDirectory.ToString()),
            });
    }
}
