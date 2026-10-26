using JetBrains.Annotations;

namespace SilverModManager.Sdk.Settings;

[PublicAPI]
public class PropertyOptions
{
    /// <summary>
    /// The section for the property to appear in.
    /// </summary>
    public required SectionId Section { get; init; }

    /// <summary>
    /// The display name of the property.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Whether changing the property requires a restart for the changes to take effect.
    /// </summary>
    public bool RequiresRestart { get; init; }

    /// <summary>
    /// An optional message when <see cref="RequiresRestart"/> is set.
    /// </summary>
    public string? RestartMessage { get; init; }

    /// <summary>
    /// Optional link for further help.
    /// </summary>
    public Uri? HelpLink { get; init; }

    /// <summary>
    /// Returns null when the setting can be changed, otherwise a short user-facing reason it can't. The settings
    /// page shows such a setting greyed out with the reason beneath its description. Evaluated once when the
    /// page builds its entries.
    /// </summary>
    public Func<string?>? DisabledReason { get; init; }
}

[PublicAPI]
public class PropertyOptions<TSettings, TProperty> : PropertyOptions
    where TSettings : class, ISettings, new()
    where TProperty : notnull
{
    /// <summary>
    /// The description of the property, generated from the current value or static.
    /// </summary>
    public required Func<TProperty, string> DescriptionFactory { get; init; }

    /// <summary>
    /// Optional validation of the current property value.
    /// </summary>
    public Func<TProperty, ValidationResult>? Validation { get; init; }
}
