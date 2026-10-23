using NexusMods.Abstractions.Secrets;
using NexusMods.Sdk.Settings;

namespace NexusMods.Secrets;

/// <summary>
/// Where sign-in tokens and passwords are kept.
/// </summary>
public record SecretsSettings : ISettings
{
    /// <summary>
    /// Only <see cref="SecretBackendChoice.Auto"/> and <see cref="SecretBackendChoice.FileOnly"/> are offered;
    /// <see cref="SecretBackendChoice.Memory"/> is for test hosts.
    /// </summary>
    public SecretBackendChoice Backend { get; init; } = SecretBackendChoice.Auto;

    public static string SettingsId => "SilverModManager.Secrets.SecretsSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json))
            .ConfigureProperty(
                x => x.Backend,
                new PropertyOptions<SecretsSettings, SecretBackendChoice>
                {
                    Section = Sections.Advanced,
                    DisplayName = "Secret storage",
                    DescriptionFactory = _ => "Auto keeps sign-in tokens in the desktop keyring (KWallet, GNOME Keyring) and only falls back to a private file when no keyring answers. File only skips the keyring; the file is readable by any program running as you. Changing this takes effect at the next start.",
                    RequiresRestart = true,
                },
                SingleValueMultipleChoiceContainerOptions.Create<SecretBackendChoice>(
                    valueComparer: EqualityComparer<SecretBackendChoice>.Default,
                    allowedValues: [SecretBackendChoice.Auto, SecretBackendChoice.FileOnly],
                    valueToDisplayString: static choice => choice switch
                    {
                        SecretBackendChoice.Auto => "Keyring, then file",
                        SecretBackendChoice.FileOnly => "File only",
                        _ => $"Unknown: {choice}",
                    }
                )
            );
    }
}
