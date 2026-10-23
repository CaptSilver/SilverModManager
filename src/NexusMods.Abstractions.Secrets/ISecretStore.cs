using R3;

namespace NexusMods.Abstractions.Secrets;

/// <summary>
/// Stores credentials. Backends report trouble through <see cref="SecretResult.Unavailable"/> rather
/// than throwing, so a missing keyring never takes the app down.
/// </summary>
public interface ISecretStore
{
    ValueTask<SecretResult> GetAsync(SecretKey key, CancellationToken cancellationToken);

    ValueTask<SecretWriteResult> SetAsync(SecretKey key, string value, CancellationToken cancellationToken);

    /// <summary>
    /// Deleting a key that is not there succeeds.
    /// </summary>
    ValueTask<SecretWriteResult> DeleteAsync(SecretKey key, CancellationToken cancellationToken);

    /// <summary>
    /// The keys the backend holds; empty when the backend cannot be read.
    /// </summary>
    ValueTask<IReadOnlyList<SecretKey>> ListAsync(CancellationToken cancellationToken);

    ValueTask<SecretStoreStatus> DescribeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Unlocks the backend. May show a prompt only in <see cref="SecretStoreMode.Interactive"/>; otherwise it
    /// is the same as <see cref="DescribeAsync"/>.
    /// </summary>
    ValueTask<SecretStoreStatus> UnlockAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Fires with the key after it was set or deleted through the store.
    /// </summary>
    Observable<SecretKey> Changed { get; }

    /// <summary>
    /// Fires when the status changes, e.g. after an unlock or when the backend goes away.
    /// </summary>
    Observable<SecretStoreStatus> StatusChanged { get; }
}

/// <summary>
/// The Secret Service (desktop keyring) backend on its own. Resolved optionally: the store chain
/// falls back to the secrets file when it is not registered.
/// </summary>
public interface ISecretServiceStore : ISecretStore;

/// <summary>
/// The store chain without the environment override layer. Migrations write through this so a
/// value supplied by an environment variable is never copied into persistent storage.
/// </summary>
public interface IPersistentSecretStore : ISecretStore;

/// <summary>
/// A store that loads its values ahead of time, so synchronous consumers can read them without blocking.
/// </summary>
public interface ISecretPrimable
{
    ValueTask PrimeAsync(CancellationToken cancellationToken);
}
