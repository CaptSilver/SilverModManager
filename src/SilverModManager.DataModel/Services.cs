using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SilverModManager.Sdk.Settings;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.GC;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Sorting;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Abstractions.Serialization.ExpressionGenerator;
using SilverModManager.DataModel.CommandLine.Verbs;
using SilverModManager.DataModel.Diagnostics;
using SilverModManager.DataModel.GameInstalled;
using SilverModManager.DataModel.JsonConverters;
using SilverModManager.DataModel.SchemaVersions;
using SilverModManager.DataModel.Sorting;
using SilverModManager.DataModel.Synchronizer;
using SilverModManager.DataModel.Synchronizer.DbFunctions;
using SilverModManager.DataModel.Undo;
using SilverModManager.Deployment.Links;
using SilverModManager.Deployment.Staging;
using SilverModManager.Deployment.Vfs;
using NexusMods.HyperDuck;
using NexusMods.HyperDuck.Adaptor.Impls.ValueAdaptor;
using SilverModManager.MnemonicDB;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Storage.Abstractions;
using SilverModManager.MnemonicDB.Storage.RocksDbBackend;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Resources;

using IFileSystem = NexusMods.Paths.IFileSystem;

namespace SilverModManager.DataModel;

/// <summary/>
public static class Services
{
    /// <summary>
    /// Adds all services related to the <see cref="DataModel"/> to your dependency
    /// injection container.
    /// </summary>
    public static IServiceCollection AddDataModel(this IServiceCollection coll)
    {
        coll.AddMnemonicDB();
        coll.AddMigrations();

        coll.AddAmbientQueriesSql();
        coll.AddSynchronizerSql();
        coll.AddSingleton<ATableFunction, IntrinsicFiles>();
        coll.AddSingleton<AScalarFunction, FNV1aHashScalar>();
        coll.AddValueAdaptor<ushort, LocationId>(LocationId.From);

        // Settings
        coll.AddSettings<DataModelSettings>();

        coll.AddSingleton<DatomStoreSettings>(sp =>
            {
                var fileSystem = sp.GetRequiredService<IFileSystem>();
                var settingsManager = sp.GetRequiredService<ISettingsManager>();
                var settings = settingsManager.Get<DataModelSettings>();
                if (settings.UseInMemoryDataModel)
                    return DatomStoreSettings.InMemory;
                
                var path = settings.MnemonicDBPath.ToPath(fileSystem);
                if (!path.DirectoryExists())
                    path.CreateDirectory();
                return new DatomStoreSettings
                {
                    Path = settings.MnemonicDBPath.ToPath(fileSystem),
                };
            }
        );
        
        coll.AddSingleton<IStoreBackend>(_ => new Backend());

        coll.AddSingleton<JsonConverter, AbsolutePathConverter>();
        coll.AddSingleton<JsonConverter, RelativePathConverter>();
        coll.AddSingleton<JsonConverter, GamePathConverter>();
        coll.AddSingleton<JsonConverter, DateTimeConverter>();
        coll.AddSingleton<JsonConverter, SizeConverter>();
        coll.AddSingleton<JsonConverterFactory, OptionalConverterFactory>();
        coll.AddSingleton<JsonConverter, OptionalConverterFactory>();

        // File Store
        coll.AddAllSingleton<IFileStore, NxFileStore>();
        
        // Readonly stream source
        coll.AddSingleton<IReadOnlyStreamSource>(s => s.GetRequiredService<NxFileStore>());
        coll.AddSingleton<IReadOnlyStreamSource, GameFileStreamSource>();
        coll.AddSingleton<IStreamSourceDispatcher, StreamSourceDispatcher>();
        
        coll.AddAllSingleton<IToolManager, ToolManager>();

        // Disk State and Synchronizer
        coll.AddDiskStateEntryModel();
        coll.AddDeploymentBackends();
        coll.AddAllSingleton<ISynchronizerService, SynchronizerService>();

        coll.AddSingleton<ITypeFinder>(_ => new AssemblyTypeFinder(typeof(Services).Assembly));
        coll.AddAllSingleton<ISorter, Sorter>();
        
        // Diagnostics
        coll.AddAllSingleton<IDiagnosticManager, DiagnosticManager>();
        coll.AddSettings<DiagnosticSettings>();
        // Only a fallback: the UI registers a writer with its own value formatters first, and that one wins.
        coll.TryAddSingleton<IDiagnosticWriter, PlainDiagnosticWriter>();
        
        // GC
        coll.AddAllSingleton<IGarbageCollectorRunner, GarbageCollectorRunner>();
        coll.AddLooseStagingStore();
        coll.AddLinkDeploymentBackends();
        coll.AddVfsCore();
        
        
        coll.AddPersistedDbResourceModel();
        
        // Undo
        coll.AddSingleton<UndoService>();

        coll.AddSingleton<ILoadoutManager, LoadoutManager>();
        coll.AddSingleton<IHostedService, InstallNexusIdBackfill>();
        coll.AddSingleton<IGameInstalledContentService, GameInstalledContentService>();
        coll.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticEmitter, GameInstalledGroupDisabledEmitter>());

        // Verbs
        coll.AddLoadoutManagementVerbs()
            .AddImportExportVerbs()
            .AddToolVerbs()
            .AddGameVerbs()
            .AddDiagnosticVerbs();

        return coll;
    }
}
