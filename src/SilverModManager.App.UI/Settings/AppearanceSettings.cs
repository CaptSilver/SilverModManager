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

    /// <summary>The reason the Glass settings can't be used on this desktop, or null when they can.</summary>
    public static string? DisabledReasonFor(string? xdgCurrentDesktop) =>
        GlassAppearance.IsBlurAvailableOn(xdgCurrentDesktop) ? null : Language.Appearance_Glass_NeedsKde;

    private static string? GlassDisabledReason() => DisabledReasonFor(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"));

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        // Json so the choice is readable before the database opens, when the first window is created.
        return settingsBuilder
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json))
            .ConfigureProperty(
                x => x.GlassEnabled,
                new PropertyOptions<AppearanceSettings, bool>
                {
                    Section = Sections.Theme,
                    DisplayName = Language.Appearance_Glass_Name,
                    DescriptionFactory = _ => Language.Appearance_Glass_Description,
                    DisabledReason = GlassDisabledReason,
                },
                new BooleanContainerOptions()
            )
            .ConfigureProperty(
                x => x.GlassTint,
                new PropertyOptions<AppearanceSettings, double>
                {
                    Section = Sections.Theme,
                    DisplayName = Language.Appearance_GlassTint_Name,
                    DescriptionFactory = _ => Language.Appearance_GlassTint_Description,
                    DisabledReason = GlassDisabledReason,
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
