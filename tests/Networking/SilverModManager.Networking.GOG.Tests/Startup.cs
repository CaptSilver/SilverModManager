using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Backend;
using SilverModManager.DataModel;
using SilverModManager.Networking.GOG;
using NexusMods.Paths;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Secrets;
using SilverModManager.Sdk.Settings;
using Xunit.DependencyInjection.Logging;

namespace SilverModManager.Networking.GOG.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services
            .AddFileSystem()
            .AddSettingsManager()
            .AddSecrets(new SecretStoreOptions { Mode = SecretStoreMode.NonInteractive, Backend = SecretBackendChoice.Memory })
            .AddGOG()
            .AddDataModel()
            .OverrideSettingsForTests<DataModelSettings>(settings => settings with { UseInMemoryDataModel = true })
            .AddLogging(builder => builder.AddXunitOutput().SetMinimumLevel(LogLevel.Debug));
    }
}
