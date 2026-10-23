using JetBrains.Annotations;

namespace SilverModManager.Sdk.Settings;

[PublicAPI]
public record OverrideHack(Type SettingsType, Func<object, object> Method, string? Key);
