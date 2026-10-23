using Microsoft.Extensions.DependencyInjection;
using NexusMods.Backend;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Settings;
using NSubstitute;

namespace NexusMods.Deployment.Staging.Tests;

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
