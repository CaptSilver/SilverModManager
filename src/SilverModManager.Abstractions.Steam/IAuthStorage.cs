namespace SilverModManager.Abstractions.Steam;

/// <summary>
/// Interface for abstracting away the storage of Steam authentication data.
/// </summary>
public interface IAuthStorage
{
    /// <summary>
    /// Loads the authentication data. <see cref="AuthLoadResult.None"/> means nothing is stored;
    /// <see cref="AuthLoadResult.Unavailable"/> means the storage could not answer, which says nothing about whether data exists.
    /// </summary>
    public Task<AuthLoadResult> TryLoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Saves the authentication data. Throws when the storage cannot take it.
    /// </summary>
    public Task SaveAsync(byte[] data, CancellationToken cancellationToken);

    /// <summary>
    /// Forgets the stored authentication data. Throws when the storage cannot be changed.
    /// </summary>
    public Task DeleteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The outcome of <see cref="IAuthStorage.TryLoadAsync"/>.
/// </summary>
public abstract record AuthLoadResult
{
    private AuthLoadResult() { }

    /// <summary>Stored data was found.</summary>
    public sealed record Loaded(byte[] Data) : AuthLoadResult
    {
        // Results end up in logs and test failure output; never print the token.
        public override string ToString() => "Loaded { Data = <redacted> }";
    }

    /// <summary>The storage answered and holds nothing.</summary>
    public sealed record None : AuthLoadResult;

    /// <summary>The storage could not answer, for example because the keyring is locked.</summary>
    public sealed record Unavailable(string Reason) : AuthLoadResult;
}
