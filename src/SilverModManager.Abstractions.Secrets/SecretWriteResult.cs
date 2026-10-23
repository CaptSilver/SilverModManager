namespace NexusMods.Abstractions.Secrets;

/// <summary>
/// The outcome of writing or deleting one secret.
/// </summary>
public abstract record SecretWriteResult
{
    private SecretWriteResult() { }

    /// <summary>The change was stored.</summary>
    public sealed record Written : SecretWriteResult;

    /// <summary>The store could not take the change right now.</summary>
    public sealed record Unavailable(SecretUnavailableKind Kind, string Reason) : SecretWriteResult;

    /// <summary>The secret is supplied from outside (e.g. an environment variable) and cannot be changed here.</summary>
    public sealed record ReadOnly(string Source) : SecretWriteResult;
}
