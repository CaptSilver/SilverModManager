using JetBrains.Annotations;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.App.UI.Settings;

public class UpdaterSettings : ISettings
{
    /// <summary>
    /// Off by default so a fresh install makes no request to GitHub until the user opts in.
    /// </summary>
    public bool ReleaseFeedEnabled { get; [UsedImplicitly] set; }

    public Version VersionToSkip { get; [UsedImplicitly] set; } = new(0, 0, 0);

    public static string SettingsId => "SilverModManager.App.UI.Settings.UpdaterSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder.ConfigureProperty(
            x => x.ReleaseFeedEnabled,
            new PropertyOptions<UpdaterSettings, bool>
            {
                Section = Sections.General,
                DisplayName = "Check GitHub for new releases",
                DescriptionFactory = _ => "When enabled, the app asks GitHub on startup whether a newer release exists",
            },
            new BooleanContainerOptions()
        );
    }
}
