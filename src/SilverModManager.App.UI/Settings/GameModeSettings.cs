using Microsoft.Extensions.DependencyInjection;
using SilverModManager.App.UI.Pages.SteamShortcut;
using SilverModManager.Platform.Gamepad;
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

    /// <summary>Whether a pad can be used for navigation. Needs a restart to take effect: the source is made once at start.</summary>
    public bool Controller { get; [JetBrains.Annotations.UsedImplicitly] set; } = true;

    /// <summary>The row's value is never changed; it is the anchor for the Show in Steam status line and button.</summary>
    public string ShowInSteam { get; [JetBrains.Annotations.UsedImplicitly] set; } = "";

    public static string SettingsId => "SilverModManager.App.UI.Settings.GameModeSettings";

    /// <summary>
    /// The reason pads cannot be read (the library did not load), or null when they can. Turning the setting off is the
    /// user's own choice and not a defect, so it gives no reason.
    /// </summary>
    public static string? DisabledReasonFor(IGamepadSource source) =>
        source is NullGamepadSource { DisabledReason: var reason } && reason != NullGamepadSource.ControllerOffReason ? reason : null;

    private static IGamepadSource? _source;

    /// <summary>Records the source the app started with, for the setting to show why it is unavailable.</summary>
    public static void ReportGamepadSource(IGamepadSource source) => Volatile.Write(ref _source, source);

    private static string? ControllerDisabledReason() => Volatile.Read(ref _source) is { } source ? DisabledReasonFor(source) : null;

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
            )
            .ConfigureProperty(
                x => x.Controller,
                new PropertyOptions<GameModeSettings, bool>
                {
                    Section = Sections.General,
                    DisplayName = "Controller navigation",
                    DescriptionFactory = _ => "Move around the app with a gamepad: D-pad to move, A to select, B to go back, X for options, Menu for the app menu.",
                    RequiresRestart = true,
                    DisabledReason = ControllerDisabledReason,
                },
                new BooleanContainerOptions()
            )
            .ConfigureProperty(
                x => x.ShowInSteam,
                new PropertyOptions<GameModeSettings, string>
                {
                    Section = Sections.General,
                    DisplayName = "Show in Steam library",
                    DescriptionFactory = _ => "Puts SilverModManager in Steam's library as a shortcut with art, so it starts from Game Mode and the Steam overlay. Steam must be closed to write it, or it can be asked to add it while it runs.",
                },
                new ActionButtonContainerOptions { CreateSource = sp => sp.GetRequiredService<SteamShortcutActionSource>() }
            );
    }
}
