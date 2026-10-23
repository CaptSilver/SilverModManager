using R3;

namespace NexusMods.Secrets.SecretService;

/// <summary>
/// The Secret Service calls the store needs, one method per D-Bus call. The store owns timeouts, retries
/// and the mapping onto <see cref="NexusMods.Abstractions.Secrets.ISecretStore"/>; an implementation only
/// talks to the bus and maps raw bus errors into <see cref="SecretServiceException"/>.
/// Every method must give up when its token is cancelled, including a wait for a prompt to complete: the
/// store's timeouts are only that token, and ksecretd without a prompter never answers at all.
/// </summary>
internal interface ISecretServiceConnection
{
    /// <summary>
    /// Opens the connection. Any failure here is reported as no bus, so it needs no mapping.
    /// </summary>
    Task ConnectAsync(string address, CancellationToken cancellationToken);

    /// <summary>
    /// The object path the <c>default</c> alias points at, or null when it points at <c>/</c>.
    /// </summary>
    Task<string?> ReadDefaultCollectionAsync(CancellationToken cancellationToken);

    Task<bool> IsLockedAsync(string collection, CancellationToken cancellationToken);

    Task<SearchResult> SearchAsync(IReadOnlyDictionary<string, string> attributes, CancellationToken cancellationToken);

    /// <summary>
    /// The attributes of one item; readable while the item is locked.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetAttributesAsync(string item, CancellationToken cancellationToken);

    Task<string> GetSecretAsync(string item, CancellationToken cancellationToken);

    /// <summary>
    /// Creates an item, replacing one with the same attributes when <paramref name="replace"/> is set.
    /// A non-null prompt means the collection is locked and nothing was stored.
    /// </summary>
    Task<CreateItemResult> CreateItemAsync(
        string collection,
        string label,
        IReadOnlyDictionary<string, string> attributes,
        string value,
        bool replace,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes an item. Returns a prompt path when the service wants the user to confirm, null when it is done.
    /// </summary>
    Task<string?> DeleteAsync(string item, CancellationToken cancellationToken);

    Task<UnlockResult> UnlockAsync(IReadOnlyList<string> objects, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a prompt and waits for it to complete. Returns true when the user dismissed it.
    /// </summary>
    Task<bool> PromptAsync(string prompt, CancellationToken cancellationToken);

    Task StartServiceByNameAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Emits the new locked state whenever the collection's <c>Locked</c> property changes.
    /// </summary>
    Observable<bool> LockedChanged(string collection);
}

internal sealed record SearchResult(IReadOnlyList<string> Unlocked, IReadOnlyList<string> Locked);

internal sealed record CreateItemResult(string? Item, string? Prompt);

internal sealed record UnlockResult(IReadOnlyList<string> Unlocked, string? Prompt);

public enum SecretServiceFailure
{
    /// <summary>Nobody owns <c>org.freedesktop.secrets</c> and the bus has no activation file for it.</summary>
    ServiceUnknown,

    /// <summary>The daemon went away mid-call.</summary>
    NoReply,

    /// <summary>The bus connection closed.</summary>
    Disconnected,

    /// <summary>The collection is locked and the service refused without offering a prompt.</summary>
    Locked,

    Other,
}

/// <summary>
/// What a connection throws instead of its transport's own exceptions, so the store never depends on D-Bus types.
/// </summary>
internal sealed class SecretServiceException(SecretServiceFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public SecretServiceFailure Failure { get; } = failure;
}
