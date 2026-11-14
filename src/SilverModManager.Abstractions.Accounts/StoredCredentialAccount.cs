using R3;
using SilverModManager.Abstractions.Secrets;

namespace SilverModManager.Abstractions.Accounts;

/// <summary>What an account found the last time it looked at where its credential is kept.</summary>
/// <param name="Status">The state and where the credential lives.</param>
/// <param name="Why">The sentence for <see cref="IModSourceAccount.WhyNotAvailable"/>; null when the row needs none.</param>
public sealed record AccountReading(AccountStatus Status, string? Why = null);

/// <summary>
/// An account whose credential sits in a store that can only be asked asynchronously. <see cref="IModSourceAccount.Status"/>
/// is synchronous, so the account reads the store when it is primed at start-up and again whenever <c>refreshOn</c>
/// fires, and answers from that last reading. Until the first reading it says <see cref="AccountState.Unavailable"/>,
/// never "signed out", so a slow keyring cannot show up as a missing login.
/// </summary>
public abstract class StoredCredentialAccount : IModSourceAccount, ISecretPrimable, IDisposable
{
    private readonly Subject<Unit> _changed = new();
    private readonly SemaphoreSlim _refreshTurn = new(1, 1);
    private readonly IDisposable _subscription;
    private volatile AccountReading _reading;

    /// <param name="notReadYet">What <see cref="WhyNotAvailable"/> says before the first reading.</param>
    /// <param name="refreshOn">Fires whenever the stored credential or the store itself may have changed.</param>
    protected StoredCredentialAccount(string notReadYet, Observable<Unit> refreshOn)
    {
        _reading = new AccountReading(new AccountStatus(AccountState.Unavailable, ""), notReadYet);
        _subscription = refreshOn.SubscribeAwait(async (_, ct) => await RefreshAsync(ct), AwaitOperation.Sequential, configureAwait: false);
    }

    public abstract string DisplayName { get; }

    public abstract AccountAction? Action { get; }

    public AccountStatus Status => _reading.Status;

    public string? WhyNotAvailable => _reading.Why;

    public Observable<Unit> Changed => _changed;

    /// <summary>Looks at the stored credential now.</summary>
    protected abstract ValueTask<AccountReading> ReadAsync(CancellationToken cancellationToken);

    /// <inheritdoc />
    public async ValueTask PrimeAsync(CancellationToken cancellationToken) => await RefreshAsync(cancellationToken);

    /// <summary>Reads the store again and announces the result when it differs from the last one.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshTurn.WaitAsync(cancellationToken);
        try
        {
            var next = await ReadAsync(cancellationToken);
            var previous = Interlocked.Exchange(ref _reading, next);
            if (previous != next) _changed.OnNext(Unit.Default);
        }
        finally
        {
            _refreshTurn.Release();
        }
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _changed.Dispose();
        _refreshTurn.Dispose();
    }
}
