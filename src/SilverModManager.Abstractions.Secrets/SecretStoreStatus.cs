namespace NexusMods.Abstractions.Secrets;

/// <summary>
/// A snapshot of a store's health, for settings UI, diagnostics and the dry-run report.
/// </summary>
/// <param name="Backend">The backend in use.</param>
/// <param name="Detail">Human-readable description, e.g. which keyring or file.</param>
/// <param name="Locked">The backend is locked and needs an unlock before it can be read.</param>
/// <param name="Unencrypted">Secrets are stored in plain text.</param>
/// <param name="Writable">A Set right now would not return Unavailable.</param>
/// <param name="OverriddenKeys">Keys whose value comes from an override and cannot be changed in the store.</param>
public sealed record SecretStoreStatus(
    SecretBackendKind Backend,
    string Detail,
    bool Locked,
    bool Unencrypted,
    bool Writable,
    IReadOnlyList<SecretKey> OverriddenKeys);
