using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Networking.Forums.Auth;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Networking.Forums;

/// <summary>
/// Whether the user can and has signed in to each forum, and how they sign in and out.
/// </summary>
public sealed class ForumLoginManager : IForumLoginState
{
    private readonly ForumOAuth _oauth;
    private readonly IForumCredentials _credentials;
    private readonly TimeProvider _time;
    private readonly IJobMonitor _jobs;
    private readonly IOSInterop _os;
    private readonly ILogger<ForumLoginManager> _logger;

    public ForumLoginManager(ForumOAuth oauth, IForumCredentials credentials, TimeProvider time, IJobMonitor jobs, IOSInterop os, ILogger<ForumLoginManager> logger)
    {
        _oauth = oauth;
        _credentials = credentials;
        _time = time;
        _jobs = jobs;
        _os = os;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanSignIn(ForumSite site) => _oauth.HasClientId(site);

    /// <inheritdoc />
    public bool IsSignedIn(ForumSite site)
        => _credentials.For(site).Current is { } token && (token.CanRefresh || !token.HasExpired(_time.GetUtcNow()));

    /// <summary>
    /// Opens the forum's sign-in in the browser and stores the token it returns. Returns quietly when the user
    /// cancels.
    /// </summary>
    /// <exception cref="SignInNotSetUpException">The site has no client id; nothing is opened.</exception>
    /// <exception cref="SignInDeclinedException">The user pressed Deny, or the forum answered with an error.</exception>
    /// <exception cref="SignInTimedOutException">Nobody finished the sign-in in time.</exception>
    public async Task SignIn(ForumSite site, CancellationToken cancellationToken)
    {
        var job = ForumLoginJob.Create(_jobs, _oauth, _os, site, _logger);
        using var _ = cancellationToken.Register(() => _jobs.Cancel(job));
        using var definition = job.JobDefinition;

        var result = await job;
        if (!result.HasValue) return;

        var written = await _credentials.For(site).SetTokenAsync(result.Value, CancellationToken.None);
        if (written is not SecretWriteResult.Written)
            _logger.LogWarning("The {Site} sign-in was not saved ({Result}); it is used until the app closes", site, written.GetType().Name);
    }

    /// <summary>
    /// Forgets the site's stored token.
    /// </summary>
    public async Task SignOut(ForumSite site, CancellationToken cancellationToken)
        => await _credentials.For(site).ClearAsync(cancellationToken);
}
