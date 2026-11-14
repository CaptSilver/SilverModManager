using R3;
using SilverModManager.Abstractions.Accounts;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Networking.Steam.Exceptions;

namespace SilverModManager.Networking.Steam;

/// <summary>
/// The stored Steam login as an account row. The row shows whether one is stored and where, signs in with a QR code
/// (through the registered handler, which is the window's overlay in the app) and forgets the login.
/// </summary>
public sealed class SteamDepotAccount : StoredCredentialAccount
{
    public const string Name = "Steam (depot files)";

    private readonly IAuthStorage _storage;
    private readonly ISecretStore _secrets;
    private readonly ISteamSession _session;

    public SteamDepotAccount(IAuthStorage storage, ISecretStore secrets, ISteamSession session)
        : base("Reading the stored Steam login.", Observable.Merge(
            secrets.Changed.Where(key => key == SteamSecretKeys.Auth).Select(_ => Unit.Default),
            secrets.StatusChanged.Select(_ => Unit.Default)))
    {
        _storage = storage;
        _secrets = secrets;
        _session = session;
    }

    public override string DisplayName => Name;

    // An unavailable store gets no button: a login made now could not be saved, and the QR code would wait for a scan nobody can use.
    public override AccountAction? Action => Status.State switch
    {
        AccountState.SignedIn => new AccountAction("Forget login", ForgetLogin),
        AccountState.SignedOut => new AccountAction("Sign in with QR code", SignIn),
        _ => null,
    };

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

    private async Task SignIn(CancellationToken cancellationToken)
    {
        try
        {
            await _session.Connect(cancellationToken);
        }
        catch (SteamLoginException e)
        {
            throw new AccountActionRefusedException(e.Message);
        }

        await RefreshAsync(cancellationToken);
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

        _session.LogOff();
        await RefreshAsync(cancellationToken);
    }
}
