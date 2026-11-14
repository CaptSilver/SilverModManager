using R3;
using SilverModManager.Abstractions.Accounts;
using SilverModManager.Abstractions.Secrets;

namespace SilverModManager.Networking.GitHub;

/// <summary>
/// The GitHub token as an account row: paste one to raise the rate limit, remove it to go back to anonymous requests.
/// </summary>
public sealed class GitHubAccount : StoredCredentialAccount
{
    /// <summary>The row's name; also what the prompt is titled, so a host without a window can point at the row.</summary>
    public const string Name = "GitHub";

    private readonly ISecretStore _secrets;
    private readonly ISecretPrompt _prompt;
    private readonly IGitHubApi _api;

    public GitHubAccount(ISecretStore secrets, ISecretPrompt prompt, IGitHubApi api)
        : base("Reading the stored GitHub token.", Observable.Merge(
            secrets.Changed.Where(key => key == GitHubSecretKeys.Token).Select(_ => Unit.Default),
            secrets.StatusChanged.Select(_ => Unit.Default)))
    {
        _secrets = secrets;
        _prompt = prompt;
        _api = api;
    }

    public override string DisplayName => Name;

    public override AccountAction? Action => Status.State switch
    {
        AccountState.SignedOut => new AccountAction("Add token", AddToken),
        AccountState.SignedIn => new AccountAction("Remove token", RemoveToken),
        _ => null,
    };

    protected override async ValueTask<AccountReading> ReadAsync(CancellationToken cancellationToken)
    {
        switch (await _secrets.GetAsync(GitHubSecretKeys.Token, cancellationToken))
        {
            case SecretResult.Found { Value.Length: > 0 }:
                var status = await _secrets.DescribeAsync(cancellationToken);
                return new AccountReading(new AccountStatus(AccountState.SignedIn, AccountSources.Describe(status, GitHubSecretKeys.Token)));
            case SecretResult.Unavailable unavailable:
                return new AccountReading(new AccountStatus(AccountState.Unavailable, ""), $"The stored GitHub token cannot be read: {unavailable.Reason}");
            default:
                return new AccountReading(new AccountStatus(AccountState.SignedOut, ""));
        }
    }

    private async Task AddToken(CancellationToken cancellationToken)
    {
        var token = await _prompt.PromptAsync(
            Name,
            "Paste a personal access token from the page below. It needs no scopes: it only lifts the limit on anonymous requests.",
            new Uri("https://github.com/settings/tokens"),
            ValidateAsync,
            cancellationToken);
        if (token is null) return;

        AccountActionRefusedException.ThrowUnlessWritten(await _secrets.SetAsync(GitHubSecretKeys.Token, token, cancellationToken), "store the GitHub token");
        await RefreshAsync(cancellationToken);
    }

    private async ValueTask<string?> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            return await _api.ValidateToken(token, cancellationToken) ? null : "GitHub did not accept this token.";
        }
        catch (HttpRequestException e)
        {
            // Not a verdict on the token: nothing is stored on a guess, and the user can try again.
            return $"Could not check the token with GitHub: {e.Message}";
        }
    }

    private async Task RemoveToken(CancellationToken cancellationToken)
    {
        AccountActionRefusedException.ThrowUnlessWritten(await _secrets.DeleteAsync(GitHubSecretKeys.Token, cancellationToken), "remove the GitHub token");
        await RefreshAsync(cancellationToken);
    }
}
