using JetBrains.Annotations;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Wabbajack;

[PublicAPI]
public record WabbajackSettings : ISettings
{
    /// <summary>
    /// Where archives that were downloaded in a browser are picked up from. This is a folder of our own, so the
    /// user's Downloads folder is never read.
    /// </summary>
    public ConfigurablePath InboxPath { get; init; }

    /// <summary>
    /// Where files of an installed modlist that have no place in the loadout (tools, configs outside the game) are
    /// kept, one folder per installation.
    /// </summary>
    public ConfigurablePath ExtrasRoot { get; init; }

    /// <inheritdoc/>
    public static string SettingsId => "SilverModManager.Wabbajack.WabbajackSettings";

    // No settings-page entry: both folders are internal working locations, not something to tune.
    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureDefault(CreateDefault)
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json));
    }

    public static WabbajackSettings CreateDefault(IServiceProvider serviceProvider)
    {
        return new WabbajackSettings
        {
            InboxPath = new ConfigurablePath(BaseKnownPath, $"{BaseDirectoryName}/{FolderName}/Inbox"),
            ExtrasRoot = new ConfigurablePath(BaseKnownPath, $"{BaseDirectoryName}/{FolderName}/Extras"),
        };
    }

    private const string FolderName = "Wabbajack";
    private const KnownPath BaseKnownPath = KnownPath.XDG_DATA_HOME;
    private const string BaseDirectoryName = ApplicationConstants.DataDirectoryName;
}
