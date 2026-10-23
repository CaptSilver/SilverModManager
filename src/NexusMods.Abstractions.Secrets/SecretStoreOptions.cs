using NexusMods.Paths;

namespace NexusMods.Abstractions.Secrets;

/// <summary>
/// Whether a store may show UI (an unlock prompt) while it works.
/// </summary>
public enum SecretStoreMode
{
    Interactive,
    NonInteractive,
}

/// <summary>
/// Which backend to use. <see cref="Auto"/> and <see cref="FileOnly"/> are the user's setting;
/// <see cref="Memory"/> is the test host override and is never offered in settings.
/// </summary>
public enum SecretBackendChoice
{
    Auto,
    FileOnly,
    Memory,
}

/// <summary>
/// Settings the store chain is built from.
/// </summary>
public sealed record SecretStoreOptions
{
    public required SecretStoreMode Mode { get; init; }

    /// <summary>
    /// Secrets file to use instead of the default location.
    /// </summary>
    public AbsolutePath? SecretsFile { get; init; }

    /// <summary>
    /// Overrides the user's backend setting when set.
    /// </summary>
    public SecretBackendChoice? Backend { get; init; }
}
