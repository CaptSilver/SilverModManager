using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Backend;
using SilverModManager.DataModel;
using NexusMods.Paths;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Networking.Thunderstore.Tests;

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
