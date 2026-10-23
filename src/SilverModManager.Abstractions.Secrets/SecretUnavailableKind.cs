namespace SilverModManager.Abstractions.Secrets;

/// <summary>
/// Why a store could not answer. Callers pick wording and recovery from this, not from the reason text.
/// </summary>
public enum SecretUnavailableKind
{
    /// <summary>No session bus address to connect to.</summary>
    NoBus,

    /// <summary>The bus is there but nothing owns org.freedesktop.secrets.</summary>
    ServiceAbsent,

    /// <summary>The service has no default collection to store items in.</summary>
    NoDefaultCollection,

    /// <summary>The collection is locked and the call was not allowed to prompt.</summary>
    Locked,

    /// <summary>The user dismissed the unlock prompt.</summary>
    Dismissed,

    /// <summary>The backend did not answer in time.</summary>
    Timeout,

    /// <summary>The backend's data could not be parsed, e.g. a bad line in the secrets file.</summary>
    Malformed,

    /// <summary>Any other backend failure.</summary>
    Faulted,
}
