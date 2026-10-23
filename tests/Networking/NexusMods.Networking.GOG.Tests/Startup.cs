using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Backend;
using NexusMods.DataModel;
using NexusMods.Networking.GOG;
using NexusMods.Paths;
using NexusMods.Abstractions.Secrets;
using NexusMods.Secrets;
using NexusMods.Sdk.Settings;
using Xunit.DependencyInjection.Logging;

namespace NexusMods.Networking.GOG.Tests;

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
