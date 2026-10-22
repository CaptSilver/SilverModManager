using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Backend;
using NexusMods.DataModel;
using NexusMods.Paths;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Settings;

namespace NexusMods.Networking.Thunderstore.Tests;

[UsedImplicitly]
public class Startup
{
    public void ConfigureServices(IServiceCollection container)
    {
        container
            .AddLogging(builder => builder.AddXUnit())
            .AddFileSystem()
            .AddDataModel()
            .AddSettingsManager()
            .OverrideSettingsForTests<DataModelSettings>(settings => settings with { UseInMemoryDataModel = true })
            .AddLibraryModels();
    }
}
