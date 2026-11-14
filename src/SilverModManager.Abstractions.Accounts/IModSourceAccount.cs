using R3;

namespace SilverModManager.Abstractions.Accounts;

public enum AccountState
{
    /// <summary>The source has no client or key the app could sign in with, so there is nothing to offer.</summary>
    NotSetUp,

    /// <summary>The app can sign in and holds no credential.</summary>
    SignedOut,

    /// <summary>The app holds a credential it can use.</summary>
    SignedIn,

    /// <summary>A credential is stored but has lapsed and cannot be renewed.</summary>
    Expired,

    /// <summary>
    /// The place credentials are kept could not be read, which says nothing about whether one exists. Never shown as
    /// "sign in again": a locked keyring is not a lapsed login.
    /// </summary>
    Unavailable,
}

/// <param name="State">Where the account stands.</param>
/// <param name="Source">Where the credential is kept, in words; "" when there is none or it is not known.</param>
public readonly record struct AccountStatus(AccountState State, string Source);

/// <summary>The button of an account row: its label and what pressing it does.</summary>
public sealed record AccountAction(string Label, Func<CancellationToken, Task> Run);

/// <summary>
/// Somewhere the user signs in or keeps a key so the app can fetch from a mod source. The Settings page shows each one
/// as a status line with one button, through <see cref="AccountButton"/>.
/// </summary>
public interface IModSourceAccount
{
    string DisplayName { get; }

    AccountStatus Status { get; }

    /// <summary>Fires after every change of what <see cref="Status"/> returns; does not replay.</summary>
    Observable<Unit> Changed { get; }

    /// <summary>The sentence shown while <see cref="AccountState.NotSetUp"/> or <see cref="AccountState.Unavailable"/>, with what the user has to do.</summary>
    string? WhyNotAvailable { get; }

    /// <summary>The one button the row offers for the current state, or null for none.</summary>
    AccountAction? Action { get; }
}
