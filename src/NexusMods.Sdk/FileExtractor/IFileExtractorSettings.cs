using JetBrains.Annotations;
using NexusMods.Paths;
using NexusMods.Sdk.Settings;

namespace NexusMods.Sdk.FileExtractor;

/// <summary>
/// Settings for the file extractor.
/// </summary>
[PublicAPI]
public record FileExtractorSettings : ISettings
{
    /// <summary>
    /// Location where the temporary folder will be stored.
    /// </summary>
    public ConfigurablePath TempFolderLocation { get; init; }

    /// <inheritdoc/>
    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureDefault(CreateDefault)
            .ConfigureBackend(StorageBackendOptions.Disable);
    }

    /// <summary>
    /// Create default.
    /// </summary>
    public static FileExtractorSettings CreateDefault(IServiceProvider serviceProvider)
    {
        // Note: The idiomatic place for this is the temporary directory (/tmp), however that is
        //       often a RAM disk and too small to handle large files.
        return new FileExtractorSettings
        {
            TempFolderLocation = new ConfigurablePath(KnownPath.XDG_STATE_HOME, $"{ApplicationConstants.DataDirectoryName}/Temp"),
        };
    }
}
