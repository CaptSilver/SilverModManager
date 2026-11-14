using R3;
using SilverModManager.Abstractions.Accounts;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Networking.Steam.Workshop;

namespace SilverModManager.Networking.Steam;

/// <summary>
/// The user's own Steam Web API key as an account row. Steam's Workshop search will not answer without one, and the app
/// ships none: the user pastes theirs, the row checks it with Steam, and without it the Workshop page lists only the
/// folder.
/// </summary>
public sealed class SteamWebApiAccount : StoredCredentialAccount
{
    /// <summary>The row's name; also what the prompt is titled, so a host without a window can point at the row.</summary>
    public const string Name = "Steam (Workshop browse)";

    private readonly ISecretStore _secrets;
    private readonly ISecretPrompt _prompt;
    private readonly ISteamWorkshopApi _api;

    public SteamWebApiAccount(ISecretStore secrets, ISecretPrompt prompt, ISteamWorkshopApi api)
        : base("Reading the stored Steam Web API key.", Observable.Merge(
            secrets.Changed.Where(key => key == SteamSecretKeys.WebApiKey).Select(_ => Unit.Default),
            secrets.StatusChanged.Select(_ => Unit.Default)))
    {
        _secrets = secrets;
        _prompt = prompt;
        _api = api;
    }

    public override string DisplayName => Name;

    public override AccountAction? Action => Status.State switch
    {
        AccountState.SignedOut => new AccountAction("Add Steam Web API key", AddKey),
        AccountState.SignedIn => new AccountAction("Remove key", RemoveKey),
        _ => null,
    };

    protected override async ValueTask<AccountReading> ReadAsync(CancellationToken cancellationToken)
    {
        switch (await _secrets.GetAsync(SteamSecretKeys.WebApiKey, cancellationToken))
        {
            case SecretResult.Found { Value.Length: > 0 }:
                var status = await _secrets.DescribeAsync(cancellationToken);
                return new AccountReading(new AccountStatus(AccountState.SignedIn, AccountSources.Describe(status, SteamSecretKeys.WebApiKey)));
            case SecretResult.Unavailable unavailable:
                return new AccountReading(new AccountStatus(AccountState.Unavailable, ""), $"The stored Steam Web API key cannot be read: {unavailable.Reason}");
            default:
                return new AccountReading(new AccountStatus(AccountState.SignedOut, ""));
        }
    }

    private async Task AddKey(CancellationToken cancellationToken)
    {
        var key = await _prompt.PromptAsync(
            Name,
            "Paste your own Steam Web API key from the page below. It is personal: keep it to yourself. It only lets the app search a game's Workshop.",
            new Uri("https://steamcommunity.com/dev/apikey"),
            ValidateAsync,
            cancellationToken);
        if (key is null) return;

        AccountActionRefusedException.ThrowUnlessWritten(await _secrets.SetAsync(SteamSecretKeys.WebApiKey, key, cancellationToken), "store the Steam Web API key");
        await RefreshAsync(cancellationToken);
    }

    private async ValueTask<string?> ValidateAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await _api.IsKeyAccepted(key, cancellationToken) ? null : "Steam did not accept this key.";
        }
        catch (SteamWorkshopUnavailableException e)
        {
            // Not a verdict on the key: nothing is stored on a guess, and the user can try again.
            return $"Could not check the key with Steam: {e.Message}";
        }
    }

    private async Task RemoveKey(CancellationToken cancellationToken)
    {
        AccountActionRefusedException.ThrowUnlessWritten(await _secrets.DeleteAsync(SteamSecretKeys.WebApiKey, cancellationToken), "remove the Steam Web API key");
        await RefreshAsync(cancellationToken);
    }
}
