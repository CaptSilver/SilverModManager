using System.Security.Cryptography;
using System.Text;
using DynamicData.Kernel;
using Microsoft.Extensions.Logging;
using R3;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Networking.Forums.Auth;

/// <summary>
/// A forum sign-in in progress, for the overlay that tells the user to finish it in the browser.
/// </summary>
public interface IForumLoginJob : IJobDefinition, IDisposable
{
    ForumSite Site { get; }

    /// <summary>
    /// The authorize page, once it has been opened, so the overlay can offer "Open the page again".
    /// </summary>
    BehaviorSubject<Uri?> LoginUri { get; }
}

/// <summary>
/// Signs in to a forum: opens the authorize page, waits on a loopback listener for the redirect, and trades the
/// code for a token. It does not store the token; <see cref="ForumLoginManager.SignIn"/> does.
/// </summary>
/// <remarks>
/// A declined or timed-out sign-in throws the listener's <see cref="SilverModManager.Sdk.Errors.UserFacingException"/>,
/// which is what the user is told. Cancelling is the user's own choice and ends with <see cref="Optional{T}.None"/>.
/// </remarks>
public sealed class ForumLoginJob : IForumLoginJob, IJobDefinitionWithStart<ForumLoginJob, Optional<OAuthToken>>
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

    private readonly ForumOAuth _oauth;
    private readonly IOSInterop _os;
    private readonly ILogger _logger;
    private readonly TimeSpan _timeout;

    public ForumSite Site { get; }

    public BehaviorSubject<Uri?> LoginUri { get; } = new(initialValue: null);

    private ForumLoginJob(ForumOAuth oauth, IOSInterop os, ForumSite site, ILogger logger, TimeSpan timeout)
    {
        _oauth = oauth;
        _os = os;
        Site = site;
        _logger = logger;
        _timeout = timeout;
    }

    /// <exception cref="SignInNotSetUpException">The site has no client id; no job is started.</exception>
    public static IJobTask<ForumLoginJob, Optional<OAuthToken>> Create(
        IJobMonitor jobMonitor, ForumOAuth oauth, IOSInterop os, ForumSite site, ILogger logger, TimeSpan? timeout = null)
    {
        oauth.RequireClientId(site);
        var job = new ForumLoginJob(oauth, os, site, logger, timeout ?? DefaultTimeout);
        return jobMonitor.Begin<ForumLoginJob, Optional<OAuthToken>>(job);
    }

    public async ValueTask<Optional<OAuthToken>> StartAsync(IJobContext<ForumLoginJob> context)
    {
        var token = context.CancellationToken;
        var endpoints = _oauth.RequireClientId(Site);
        var state = RandomToken();
        var verifier = endpoints.UsePkce ? RandomToken() : null;
        var challenge = verifier is null ? null : Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        using var listener = new LoopbackCallbackListener(_logger);
        try
        {
            // Wait first so the redirect cannot arrive before anybody is listening for it.
            var callback = listener.WaitAsync(state, Site.DisplayName(), _timeout, token);

            // The authorize URL carries the state and challenge, so it goes to the browser and the overlay only.
            // If opening it throws, leaving closes the listener under the wait, which then ends as cancelled.
            var authorize = _oauth.BuildAuthorizeUri(Site, listener.RedirectUri, state, challenge);
            LoginUri.OnNext(authorize);
            _os.OpenUri(authorize);

            var code = (await callback).Code;
            return await _oauth.ExchangeCode(Site, code, verifier, listener.RedirectUri, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return Optional<OAuthToken>.None;
        }
    }

    public void Dispose() => LoginUri.Dispose();

    private static string RandomToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
