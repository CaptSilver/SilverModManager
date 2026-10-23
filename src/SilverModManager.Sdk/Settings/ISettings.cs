using JetBrains.Annotations;

namespace NexusMods.Sdk.Settings;

/// <summary>
/// Interface for settings.
/// </summary>
[PublicAPI]
public interface ISettings
{
    /// <summary>
    /// Stable identifier that names this type's settings file or database row. It is a
    /// literal on purpose: deriving it from the namespace would re-key every stored setting
    /// whenever the type moves.
    /// </summary>
    static abstract string SettingsId { get; }

    /// <summary>
    /// Configuration method for this type.
    /// </summary>
    static abstract ISettingsBuilder Configure(ISettingsBuilder settingsBuilder);
}
