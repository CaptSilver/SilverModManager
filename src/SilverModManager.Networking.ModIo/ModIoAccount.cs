using R3;
using SilverModManager.Abstractions.Accounts;
using SilverModManager.Abstractions.Secrets;

namespace SilverModManager.Networking.ModIo;

/// <summary>
/// The user's own mod.io access token as an account row. mod.io answers nothing without a token and the app ships none:
/// the user creates one at mod.io/me/access with the read and write scopes and pastes it, and the row checks it with
/// mod.io before keeping it.
/// </summary>
public sealed class ModIoAccount : StoredCredentialAccount
{
    /// <summary>The row's name; also what the prompt is titled, so a host without a window can point at the row.</summary>
    public const string Name = "mod.io";

    private readonly ISecretStore _secrets;
    private readonly ISecretPrompt _prompt;
    private readonly IModIoApi _api;

    public ModIoAccount(ISecretStore secrets, ISecretPrompt prompt, IModIoApi api)
        : base("Reading the stored mod.io token.", Observable.Merge(
            secrets.Changed.Where(key => key == ModIoSecretKeys.Token).Select(_ => Unit.Default),
            secrets.StatusChanged.Select(_ => Unit.Default)))
    {
        _secrets = secrets;
        _prompt = prompt;
        _api = api;
    }

    public override string DisplayName => Name;

    public override AccountAction? Action => Status.State switch
    {
        AccountState.SignedOut => new AccountAction("Paste access token", AddToken),
        AccountState.SignedIn => new AccountAction("Remove token", RemoveToken),
        _ => null,
    };

    protected override async ValueTask<AccountReading> ReadAsync(CancellationToken cancellationToken)
    {
        switch (await _secrets.GetAsync(ModIoSecretKeys.Token, cancellationToken))
        {
            case SecretResult.Found { Value.Length: > 0 }:
                var status = await _secrets.DescribeAsync(cancellationToken);
                return new AccountReading(new AccountStatus(AccountState.SignedIn, AccountSources.Describe(status, ModIoSecretKeys.Token)));
            case SecretResult.Unavailable unavailable:
                return new AccountReading(new AccountStatus(AccountState.Unavailable, ""), $"The stored mod.io token cannot be read: {unavailable.Reason}");
            default:
                return new AccountReading(new AccountStatus(AccountState.SignedOut, ""));
        }
    }

    private async Task AddToken(CancellationToken cancellationToken)
    {
        var token = await _prompt.PromptAsync(
            Name,
            "Paste an access token from the page below. Create it with both the read and the write scope: without write the app cannot subscribe you to mods. It is personal: keep it to yourself.",
            new Uri("https://mod.io/me/access"),
            ValidateAsync,
            cancellationToken);
        if (token is null) return;

        // The value checked is the trimmed one, so that is what is kept: a pasted trailing newline is no header value.
        AccountActionRefusedException.ThrowUnlessWritten(await _secrets.SetAsync(ModIoSecretKeys.Token, token.Trim(), cancellationToken), "store the mod.io token");
        await RefreshAsync(cancellationToken);
    }

    private async ValueTask<string?> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            await _api.Me(token.Trim(), cancellationToken);
            return null;
        }
        catch (ModIoTokenRejectedException)
        {
            return "mod.io did not accept this token.";
        }
        catch (Exception e) when (e is ModIoUnavailableException or ModIoRateLimitedException or ModIoRefusedException or ModIoTermsNotAcceptedException)
        {
            // Not a verdict on the token that the user can fix by pasting another: nothing is stored on a guess.
            return $"Could not check the token with mod.io: {e.Message}";
        }
    }

    private async Task RemoveToken(CancellationToken cancellationToken)
    {
        AccountActionRefusedException.ThrowUnlessWritten(await _secrets.DeleteAsync(ModIoSecretKeys.Token, cancellationToken), "remove the mod.io token");
        await RefreshAsync(cancellationToken);
    }
}
