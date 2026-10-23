using JetBrains.Annotations;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.App.UI.Settings;

/// <summary>
/// Settings that give access to experimental features in the UI.
/// </summary>
public record ExperimentalSettings : ISettings
{
    // TODO: remove for GA
    public bool EnableCollectionSharing { get; [UsedImplicitly] set; }

    public static string SettingsId => "SilverModManager.App.UI.Settings.ExperimentalSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json))
            .ConfigureProperty(
                x => x.EnableCollectionSharing,
                new PropertyOptions<ExperimentalSettings, bool>
                {
                    Section = Sections.Experimental,
                    DisplayName = "Enable sharing collections",
                    DescriptionFactory = _ => "Allows uploading of collections",
                },
                new BooleanContainerOptions()
            );
    }
}
