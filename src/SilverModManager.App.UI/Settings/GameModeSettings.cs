using SilverModManager.Sdk.Settings;

namespace SilverModManager.App.UI.Settings;

/// <summary>
/// How big the controls are: Desktop for a mouse at arm's length, Couch for a controller and a TV or a handheld.
/// </summary>
public enum UiDensity
{
    /// <summary>Couch in Game Mode, Desktop everywhere else.</summary>
    Auto,

    Desktop,

    Couch,
}

public record GameModeSettings : ISettings
{
    public UiDensity Density { get; [JetBrains.Annotations.UsedImplicitly] set; } = UiDensity.Auto;

    public static string SettingsId => "SilverModManager.App.UI.Settings.GameModeSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        // Json so the choice is readable before the database opens, when the first window is created.
        return settingsBuilder
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json))
            .ConfigureProperty(
                x => x.Density,
                new PropertyOptions<GameModeSettings, UiDensity>
                {
                    Section = Sections.Theme,
                    DisplayName = "Interface size",
                    DescriptionFactory = _ => "Couch makes text and rows larger for a controller or a TV. Auto picks Couch in Game Mode and Desktop otherwise.",
                    RequiresRestart = true,
                },
                SingleValueMultipleChoiceContainerOptions.Create(
                    valueComparer: EqualityComparer<UiDensity>.Default,
                    allowedValues: [UiDensity.Auto, UiDensity.Desktop, UiDensity.Couch],
                    valueToDisplayString: static density => density switch
                    {
                        UiDensity.Auto => "Auto",
                        UiDensity.Desktop => "Desktop",
                        UiDensity.Couch => "Couch",
                        _ => throw new ArgumentOutOfRangeException(nameof(density), density, null),
                    }
                )
            );
    }
}
