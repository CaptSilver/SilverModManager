using JetBrains.Annotations;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.App.UI.Settings;

public record AppearanceSettings : ISettings
{
    /// <summary>Off by default: the window is opaque until the user asks for the desktop to show through.</summary>
    public bool GlassEnabled { get; [UsedImplicitly] set; }

    /// <summary>How opaque the window colour is over the blurred desktop, from 0 to 1. See <see cref="GlassAppearance.ClampTint"/>.</summary>
    public double GlassTint { get; [UsedImplicitly] set; } = GlassAppearance.DefaultTint;

    public static string SettingsId => "SilverModManager.App.UI.Settings.AppearanceSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        // Json so the choice is readable before the database opens, when the first window is created.
        return settingsBuilder
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json))
            .ConfigureProperty(
                x => x.GlassEnabled,
                new PropertyOptions<AppearanceSettings, bool>
                {
                    Section = Sections.General,
                    DisplayName = Language.Appearance_Glass_Name,
                    DescriptionFactory = _ => Language.Appearance_Glass_Description,
                },
                new BooleanContainerOptions()
            )
            .ConfigureProperty(
                x => x.GlassTint,
                new PropertyOptions<AppearanceSettings, double>
                {
                    Section = Sections.General,
                    DisplayName = Language.Appearance_GlassTint_Name,
                    DescriptionFactory = _ => Language.Appearance_GlassTint_Description,
                },
                new SliderContainerOptions(
                    GlassAppearance.MinTint,
                    GlassAppearance.MaxTint,
                    tickFrequency: 0.05,
                    valueToDisplayString: value => $"{Math.Round(value * 100):0}%"
                )
            );
    }
}
