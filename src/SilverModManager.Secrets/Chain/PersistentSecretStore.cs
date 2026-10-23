using NexusMods.Abstractions.Secrets;
using R3;

namespace NexusMods.Secrets.Chain;

/// <summary>
/// The store chain under the overlay, handed to code that must write through an environment override.
/// </summary>
internal sealed class PersistentSecretStore(ISecretStore inner) : IPersistentSecretStore
{
    public ValueTask<SecretResult> GetAsync(SecretKey key, CancellationToken cancellationToken) => inner.GetAsync(key, cancellationToken);

    public ValueTask<SecretWriteResult> SetAsync(SecretKey key, string value, CancellationToken cancellationToken) => inner.SetAsync(key, value, cancellationToken);

    public ValueTask<SecretWriteResult> DeleteAsync(SecretKey key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);

    public ValueTask<IReadOnlyList<SecretKey>> ListAsync(CancellationToken cancellationToken) => inner.ListAsync(cancellationToken);

    public ValueTask<SecretStoreStatus> DescribeAsync(CancellationToken cancellationToken) => inner.DescribeAsync(cancellationToken);

    public ValueTask<SecretStoreStatus> UnlockAsync(CancellationToken cancellationToken) => inner.UnlockAsync(cancellationToken);

    public Observable<SecretKey> Changed => inner.Changed;

    public Observable<SecretStoreStatus> StatusChanged => inner.StatusChanged;
}
