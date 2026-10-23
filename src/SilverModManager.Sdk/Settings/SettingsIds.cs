using JetBrains.Annotations;

namespace NexusMods.Sdk.Settings;

/// <summary>
/// Builds the storage name of a settings value from <see cref="ISettings.SettingsId"/>.
/// </summary>
[PublicAPI]
public static class SettingsIds
{
    /// <summary>
    /// The id alone when there is no key, otherwise the id and key joined by <paramref name="separator"/>.
    /// </summary>
    public static string For<T>(string? key, char separator) where T : ISettings
    {
        return string.IsNullOrWhiteSpace(key) ? T.SettingsId : $"{T.SettingsId}{separator}{key}";
    }
}
