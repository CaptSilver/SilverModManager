using R3;
using SilverModManager.Abstractions.Accounts;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Abstractions.Steam;

namespace SilverModManager.Networking.Steam;

/// <summary>
/// The stored Steam login as an account row. The login itself is made from a terminal (`steam login`); the row shows
/// whether one is stored and where, and forgets it.
/// </summary>
public sealed class SteamDepotAccount : StoredCredentialAccount
{
    public const string Name = "Steam (depot files)";

    private readonly IAuthStorage _storage;
    private readonly ISecretStore _secrets;

    public SteamDepotAccount(IAuthStorage storage, ISecretStore secrets)
        : base("Reading the stored Steam login.", Observable.Merge(
            secrets.Changed.Where(key => key == SteamSecretKeys.Auth).Select(_ => Unit.Default),
            secrets.StatusChanged.Select(_ => Unit.Default)))
    {
        _storage = storage;
        _secrets = secrets;
    }

    public override string DisplayName => Name;

    // Signing in has no button until the row can show a QR code; the Settings row's description names the terminal command.
    public override AccountAction? Action => Status.State == AccountState.SignedIn ? new AccountAction("Forget login", ForgetLogin) : null;

    protected override async ValueTask<AccountReading> ReadAsync(CancellationToken cancellationToken)
    {
        switch (await _storage.TryLoadAsync(cancellationToken))
        {
            case AuthLoadResult.Loaded:
                var status = await _secrets.DescribeAsync(cancellationToken);
                return new AccountReading(new AccountStatus(AccountState.SignedIn, AccountSources.Describe(status, SteamSecretKeys.Auth)));
            case AuthLoadResult.Unavailable unavailable:
                return new AccountReading(new AccountStatus(AccountState.Unavailable, ""), $"The stored Steam login cannot be read: {unavailable.Reason}");
            default:
                return new AccountReading(new AccountStatus(AccountState.SignedOut, ""));
        }
    }

    private async Task ForgetLogin(CancellationToken cancellationToken)
    {
        try
        {
            await _storage.DeleteAsync(cancellationToken);
        }
        catch (InvalidOperationException e)
        {
            // The storage words its own refusal ("Could not delete the Steam login: ...").
            throw new AccountActionRefusedException(e.Message);
        }

        await RefreshAsync(cancellationToken);
    }
}
