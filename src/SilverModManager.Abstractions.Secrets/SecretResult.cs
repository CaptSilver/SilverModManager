namespace SilverModManager.Abstractions.Secrets;

/// <summary>
/// The outcome of reading one secret.
/// </summary>
public abstract record SecretResult
{
    private SecretResult() { }

    /// <summary>The secret exists.</summary>
    public sealed record Found(string Value) : SecretResult
    {
        // Results end up in logs and test failure output; never print the value.
        public override string ToString() => "Found { Value = <redacted> }";
    }

    /// <summary>The store answered and holds no such secret.</summary>
    public sealed record NotFound : SecretResult;

    /// <summary>The store could not answer; says nothing about whether the secret exists.</summary>
    public sealed record Unavailable(SecretUnavailableKind Kind, string Reason) : SecretResult;
}
