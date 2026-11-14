using R3;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Abstractions.Accounts;

/// <summary>
/// Renders any <see cref="IModSourceAccount"/> as a Settings row, so every account says the same things in the same
/// words and no source writes its own copy of this switch.
/// </summary>
public sealed class AccountButton : IActionButtonSource
{
    private readonly IModSourceAccount _account;

    public AccountButton(IModSourceAccount account)
    {
        _account = account;
        // Deferred so a subscriber that comes later starts from the state of that moment.
        State = Observable.Defer(() => account.Changed.Select(_ => Current).Prepend(Current));
    }

    /// <inheritdoc />
    public Observable<ActionButtonState> State { get; }

    /// <inheritdoc />
    public ActionButtonState Current
    {
        get
        {
            var status = _account.Status;
            return status.State switch
            {
                AccountState.NotSetUp or AccountState.Unavailable => new ActionButtonState(Why(), null),
                AccountState.SignedOut => new ActionButtonState("Not signed in", _account.Action?.Label),
                AccountState.SignedIn => new ActionButtonState(status.Source == "" ? "Signed in." : $"Signed in. Stored in {status.Source}.", _account.Action?.Label),
                AccountState.Expired => new ActionButtonState("Session expired. Sign in again.", _account.Action?.Label),
                _ => throw new ArgumentOutOfRangeException(nameof(status), status.State, "Unknown account state"),
            };
        }
    }

    /// <inheritdoc />
    public Task Invoke(CancellationToken cancellationToken)
    {
        // Not offered while the account cannot act, so reaching here with no button is a caller bug, not a no-op.
        var state = _account.Status.State;
        if (state is AccountState.NotSetUp or AccountState.Unavailable || _account.Action is not { } action)
            throw new InvalidOperationException($"The {_account.DisplayName} row offers no action while it is {state}");
        return action.Run(cancellationToken);
    }

    private string Why() => _account.WhyNotAvailable ?? $"{_account.DisplayName} is not available.";
}
