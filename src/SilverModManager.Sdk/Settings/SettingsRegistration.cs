using JetBrains.Annotations;

namespace SilverModManager.Sdk.Settings;

[PublicAPI]
public record SettingsRegistration(
    Type ObjectType,
    ISettings DefaultValue,
    Func<ISettingsBuilder, ISettingsBuilder> ConfigureLambda
);
