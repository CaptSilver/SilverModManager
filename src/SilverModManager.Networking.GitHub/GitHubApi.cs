using System.Net.Http.Headers;
using System.Net.Http.Json;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Networking.GitHub.DTOs;

namespace SilverModManager.Networking.GitHub;

[PublicAPI]
internal class GitHubApi : IGitHubApi
{
    private const string BaseUrl = "https://api.github.com";

    // https://docs.github.com/en/rest/about-the-rest-api/api-versions
    private const string HeaderApiVersion = "X-GitHub-Api-Version";
    private const string ApiVersion = "2022-11-28";

    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly ISecretStore _secrets;
    private readonly TimeProvider _time;

    // Set when a reply says no requests are left; until then nothing is sent, because every refused request still
    // counts against the budget GitHub is about to refill. Anonymous requests and requests with a token draw on
    // separate budgets, so a token added after the anonymous one ran out is used at once.
    private long _anonymousLimitedUntilUnix;
    private long _authenticatedLimitedUntilUnix;

    public AuthenticationHeaderValue? AuthenticationHeader { get; set; }

    public GitHubApi(ILogger<GitHubApi> logger, HttpClient httpClient, ISecretStore secrets, TimeProvider? time = null)
    {
        _logger = logger;
        _httpClient = httpClient;
        _secrets = secrets;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// The GitHub token the secret store holds or an override supplies (<c>GITHUB_TOKEN</c> is an alias of the key),
    /// or null when there is none or the store cannot answer. A lookup that fails must not fail the request it is for.
    /// </summary>
    public async ValueTask<string?> TryGetTokenAsync(CancellationToken cancellationToken = default)
    {
        var result = await _secrets.GetAsync(GitHubSecretKeys.Token, cancellationToken).ConfigureAwait(false);
        switch (result)
        {
            case SecretResult.Found { Value: { Length: > 0 } token }:
                return token;
            case SecretResult.Unavailable unavailable:
                _logger.LogDebug("No GitHub token: the secret store is unavailable ({Kind}: {Reason})", unavailable.Kind, unavailable.Reason);
                return null;
            default:
                return null;
        }
    }

    // A header set on the instance wins; otherwise a token from the store makes the request authenticated.
    private async ValueTask AuthenticateAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (AuthenticationHeader is not null)
        {
            request.Headers.Authorization = AuthenticationHeader;
            return;
        }

        if (await TryGetTokenAsync(cancellationToken).ConfigureAwait(false) is { } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    // Set once a token has been refused, so the warning isn't repeated on every request.
    private int _reportedRejectedToken;

    /// <summary>
    /// Sends a GET to <paramref name="uri"/>, authenticated when there's a token. If GitHub refuses the token (401, a
    /// stale or revoked token), the request is sent again without it: everything this client reads is public, so a dead
    /// token must not break update checks or release downloads.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        var (response, authenticated) = await SendOnceAsync(uri, authenticate: true, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized || !authenticated)
            return response;

        if (Interlocked.Exchange(ref _reportedRejectedToken, 1) == 0)
            _logger.LogWarning("GitHub refused the GitHub token (GITHUB_TOKEN or the stored github.token), so requests go without it. Replace or remove the token to stop this");

        response.Dispose();
        var (anonymous, _) = await SendOnceAsync(uri, authenticate: false, cancellationToken).ConfigureAwait(false);
        return anonymous;
    }

    // Also says whether the request carried a token: a handler need not fill in the response's RequestMessage.
    private async Task<(HttpResponseMessage Response, bool Authenticated)> SendOnceAsync(Uri uri, bool authenticate, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add(HeaderApiVersion, ApiVersion);
        if (authenticate) await AuthenticateAsync(request, cancellationToken).ConfigureAwait(false);
        var authenticated = request.Headers.Authorization is not null;
        ThrowIfLimited(authenticated);
        var response = await _httpClient.SendAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            ObserveRateLimit(response, authenticated);
        }
        catch
        {
            response.Dispose();
            throw;
        }

        return (response, authenticated);
    }

    private ref long LimitedUntilUnix(bool authenticated) => ref authenticated ? ref _authenticatedLimitedUntilUnix : ref _anonymousLimitedUntilUnix;

    private void ThrowIfLimited(bool authenticated)
    {
        var until = DateTimeOffset.FromUnixTimeSeconds(Volatile.Read(ref LimitedUntilUnix(authenticated)));
        if (_time.GetUtcNow() < until) throw new GitHubRateLimitedException(until);
    }

    // https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api: x-ratelimit-reset is a Unix time in
    // seconds. An answer that took the last request is still an answer; only a refusal with an empty budget throws.
    private void ObserveRateLimit(HttpResponseMessage response, bool authenticated)
    {
        if (!TryReadHeader(response, "x-ratelimit-remaining", out var remaining)) return;
        if (remaining > 0)
        {
            Volatile.Write(ref LimitedUntilUnix(authenticated), 0);
            return;
        }

        if (!TryReadHeader(response, "x-ratelimit-reset", out var reset)) return;

        var until = DateTimeOffset.FromUnixTimeSeconds(reset);
        Volatile.Write(ref LimitedUntilUnix(authenticated), reset);
        _logger.LogWarning("GitHub reports no requests left until {Reset}", until);
        if (!response.IsSuccessStatusCode) throw new GitHubRateLimitedException(until);
    }

    private static bool TryReadHeader(HttpResponseMessage response, string name, out long value)
    {
        value = 0;
        return response.Headers.TryGetValues(name, out var values)
            && long.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    public async ValueTask<bool> ValidateToken(string token, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{BaseUrl}/user"));
        request.Headers.Add(HeaderApiVersion, ApiVersion);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async ValueTask<Release[]?> FetchReleases(string organization, string repository, CancellationToken cancellationToken = default)
    {
        var uri = new Uri($"{BaseUrl}/repos/{organization}/{repository}/releases");
        _logger.LogDebug("Fetching releases from {Uri}", uri);

        try
        {
            using var response = await SendAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GitHub Api returned with status code `{Code}`", response.StatusCode);
                return null;
            }

            var releases = await response.Content.ReadFromJsonAsync<Release[]>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (releases is null)
            {
                _logger.LogWarning("Failed to deserialize releases from GitHub for `{Organization}/{Repository}`", organization, repository);
                return null;
            }

            _logger.LogDebug("Fetched `{Count}` release(s) from GitHub for `{Organization}/{Repository}`", releases.Length, organization, repository);
            return releases;
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (GitHubRateLimitedException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Exception fetching releases from GitHub for `{Organization}/{Repository}`", organization, repository);
        }

        return null;
    }

    public async ValueTask<Release?> FetchLatestRelease(string organization, string repository, IComparer<Release>? comparer = null, CancellationToken cancellationToken = default)
    {
        comparer ??= ReleaseTagComparer.Instance;

        var releases = await FetchReleases(organization, repository, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (releases is null) return null;

        if (releases.Length > 1) Array.Sort(releases, comparer);
        var latest = releases[^1];

        _logger.LogDebug("Latest release from GitHub for `{Organization}/{Repository}` is `{Name}`", organization, repository, latest.Name);
        return latest;
    }

    public ValueTask<Release?> FetchReleaseByTag(string organization, string repository, string tag, CancellationToken cancellationToken = default)
    {
        return FetchRelease(new Uri($"{BaseUrl}/repos/{organization}/{repository}/releases/tags/{Uri.EscapeDataString(tag)}"), cancellationToken);
    }

    public ValueTask<Release?> FetchMarkedLatestRelease(string organization, string repository, CancellationToken cancellationToken = default)
    {
        return FetchRelease(new Uri($"{BaseUrl}/repos/{organization}/{repository}/releases/latest"), cancellationToken);
    }

    public ValueTask<Release?> FetchLatestReleaseById(long repositoryId, CancellationToken cancellationToken = default)
    {
        return FetchRelease(new Uri($"{BaseUrl}/repositories/{repositoryId}/releases/latest"), cancellationToken);
    }

    public ValueTask<Release?> FetchReleaseByTagById(long repositoryId, string tag, CancellationToken cancellationToken = default)
    {
        return FetchRelease(new Uri($"{BaseUrl}/repositories/{repositoryId}/releases/tags/{Uri.EscapeDataString(tag)}"), cancellationToken);
    }

    private async ValueTask<Release?> FetchRelease(Uri uri, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Fetching release from {Uri}", uri);

        try
        {
            using var response = await SendAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GitHub Api returned with status code `{Code}` for `{Uri}`", response.StatusCode, uri);
                return null;
            }

            var release = await response.Content.ReadFromJsonAsync<Release>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (release is null) _logger.LogWarning("Failed to deserialize release from `{Uri}`", uri);
            return release;
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (GitHubRateLimitedException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Exception fetching release from `{Uri}`", uri);
        }

        return null;
    }

    public async ValueTask<ulong?> FetchRepositoryId(string organization, string repository, CancellationToken cancellationToken = default)
    {
        var uri = new Uri($"{BaseUrl}/repos/{organization}/{repository}");
        _logger.LogDebug("Fetching repository from {Uri}", uri);

        try
        {
            using var response = await SendAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GitHub Api returned with status code `{Code}` for `{Uri}`", response.StatusCode, uri);
                return null;
            }

            using var document = await System.Text.Json.JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.TryGetProperty("id", out var id) && id.TryGetUInt64(out var value) ? value : null;
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (GitHubRateLimitedException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Exception fetching repository from `{Uri}`", uri);
        }

        return null;
    }
}
