using JetBrains.Annotations;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;

namespace NexusMods.DataModel;

/// <summary>
/// Settings for the data model.
/// </summary>
[PublicAPI]
public record DataModelSettings : ISettings
{
    private const string DataModelFolderName = "DataModel";
    
    /// <summary>
    /// If true, data model will be stored in memory only and the paths will be ignored.
    /// </summary>
    public bool UseInMemoryDataModel { get; set; }
    
    /// <summary>
    /// Path of the folder containing the MnemonicDB database.
    /// </summary>
    public ConfigurablePath MnemonicDBPath { get; set; }

    /// <summary>
    /// Preconfigured locations [full paths] where mod archives can/will be stored.
    /// </summary>
    public ConfigurablePath[] ArchiveLocations { get; set; } = [];

    /// <inheritdoc/>
    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureDefault(CreateDefault)
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json))
            .ConfigureProperty(
                x => x.ArchiveLocations,
                new PropertyOptions<DataModelSettings, ConfigurablePath[]>
                {
                    Section = Sections.General,
                    DisplayName = "Storage Location",
                    DescriptionFactory = currentPath => $"""
                          Mods and backups are stored in:  
                          **{currentPath[0].ToPath(FileSystem.Shared)}**
                          
                          *Important: Existing files won’t move automatically when you change storage location, you’ll need to move them manually.*
                          """,
                    RequiresRestart = true,
                },
                new ConfigurablePathsContainerOption()
            );
    }

    /// <summary>
    /// Create default value.
    /// </summary>
    public static DataModelSettings CreateDefault(IServiceProvider serviceProvider)
    {
        return new DataModelSettings
        {
            MnemonicDBPath = new ConfigurablePath(BaseKnownPath, $"{BaseDirectoryName}/{DataModelFolderName}/MnemonicDB.rocksdb"),
            ArchiveLocations = [
                new ConfigurablePath(BaseKnownPath, $"{BaseDirectoryName}/{DataModelFolderName}/Archives"),
            ],
        };
    }

    /// <summary>
    /// Retrieves the base directory where the App stores its local application data.
    /// </summary>
    /// <returns>The absolute path to the local application data directory.</returns>
    public static AbsolutePath GetLocalApplicationDataDirectory(IFileSystem fs)
    {
        return fs.GetKnownPath(BaseKnownPath).Combine(BaseDirectoryName);
    }

    /// <summary>
    /// Retrieves the default DataModel folder.
    /// This folder is reserved for the App and should not store user info.
    /// </summary>
    public static AbsolutePath GetStandardDataModelFolder(IFileSystem fs)
    {
        return GetLocalApplicationDataDirectory(fs).Combine(DataModelFolderName);
    }

    private const KnownPath BaseKnownPath = KnownPath.XDG_DATA_HOME;
    private const string BaseDirectoryName = ApplicationConstants.DataDirectoryName;
}
