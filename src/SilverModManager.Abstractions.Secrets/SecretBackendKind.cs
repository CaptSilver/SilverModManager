namespace NexusMods.Abstractions.Secrets;

/// <summary>
/// Which backend currently holds the secrets.
/// </summary>
public enum SecretBackendKind
{
    /// <summary>Nothing usable.</summary>
    None,

    /// <summary>The desktop keyring over the Secret Service D-Bus API.</summary>
    SecretService,

    /// <summary>A secrets file in the data folder.</summary>
    File,

    /// <summary>Values supplied through environment variables or the secrets-file override.</summary>
    Override,

    /// <summary>In-process only; used by tests.</summary>
    Memory,
}
