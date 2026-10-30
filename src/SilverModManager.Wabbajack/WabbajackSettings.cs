using JetBrains.Annotations;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Wabbajack;

[PublicAPI]
public record WabbajackSettings : ISettings
{
    /// <summary>
    /// Where archives that were downloaded in a browser are picked up from. This is a folder of our own. The user's
    /// Downloads folder is only read when <see cref="PickUpFromDownloads"/> is on, and then only for the names the
    /// current plan expects.
    /// </summary>
    public ConfigurablePath InboxPath { get; init; }

    /// <summary>Whether finished browser downloads are picked up from the Downloads folder. Off unless the user turns it on.</summary>
    public bool PickUpFromDownloads { get; init; }

    /// <summary>The folder to watch when pickup is on. Empty means the XDG download folder.</summary>
    public string DownloadsFolder { get; init; } = "";

    /// <summary>
    /// Where files of an installed modlist that have no place in the loadout (tools, configs outside the game) are
    /// kept, one folder per installation.
    /// </summary>
    public ConfigurablePath ExtrasRoot { get; init; }

    /// <summary>
    /// Where the downloaded copy of Wabbajack's public gallery is kept. It is a cache: deleting it only costs a
    /// fetch.
    /// </summary>
    public ConfigurablePath GalleryCachePath { get; init; }

    /// <inheritdoc/>
    public static string SettingsId => "SilverModManager.Wabbajack.WabbajackSettings";

    // No settings-page entry: all of these are internal working locations, not something to tune.
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
            GalleryCachePath = new ConfigurablePath(KnownPath.XDG_CACHE_HOME, $"{BaseDirectoryName}/{FolderName}/Gallery"),
        };
    }

    private const string FolderName = "Wabbajack";
    private const KnownPath BaseKnownPath = KnownPath.XDG_DATA_HOME;
    private const string BaseDirectoryName = ApplicationConstants.DataDirectoryName;
}
