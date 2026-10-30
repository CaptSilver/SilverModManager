using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SilverModManager.Networking.Forums.Auth;
using SilverModManager.Networking.HttpDownloader;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Networking.Forums.Downloads;

/// <summary>
/// One file of a forum download, as the files API lists it.
/// </summary>
public sealed record ForumFile(string Name, Uri Url, long? Size);

/// <summary>
/// What the files API lists under one download.
/// </summary>
public sealed record FileListing(IReadOnlyList<ForumFile> Files);

/// <summary>
/// Looks files up in a forum's signed-in downloads API, so a download can start from the address the forum lists
/// for the file instead of from its web page.
/// </summary>
/// <remarks>
/// The client sends the user's token, so it must be built on an <see cref="HttpClient"/> that does not log request
/// URLs or headers. Nothing it logs contains a token.
/// </remarks>
public sealed class ForumFilesClient
{
    private static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private readonly ForumOAuth _oauth;
    private readonly IForumCredentials _credentials;
    private readonly TimeProvider _time;
    private readonly ILogger<ForumFilesClient> _logger;

    public ForumFilesClient(HttpClient http, ForumOAuth oauth, IForumCredentials credentials, TimeProvider time, ILogger<ForumFilesClient> logger)
    {
        _http = http;
        _oauth = oauth;
        _credentials = credentials;
        _time = time;
        _logger = logger;
    }

    /// <summary>How many times a lookup is made before a flaky answer is given up on.</summary>
    public int Attempts { get; init; } = 4;

    /// <summary>The pause before the first retry; each one after waits twice as long as the last.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The files the forum lists under download <paramref name="id"/>.
    /// </summary>
    /// <exception cref="NotSignedInException">The site has no stored token.</exception>
    /// <exception cref="SignInAgainException">The token is no longer good and could not be renewed.</exception>
    /// <exception cref="ForumRefusedException">The forum answered 403.</exception>
    /// <exception cref="ForumRateLimitedException">The forum kept answering 429.</exception>
    public Task<FileListing> GetFiles(ForumSite site, long id, CancellationToken cancellationToken)
        => DownloadRetries.Run(_logger, $"the {site.DisplayName()} file listing", Attempts, RetryDelay, () => Attempt(site, id, cancellationToken), cancellationToken, _time);

    /// <summary>
    /// The address the forum lists for the file called <paramref name="fileName"/> under download <paramref name="id"/>,
    /// checked against the size the modlist expects before anything is fetched.
    /// </summary>
    /// <exception cref="ForumFileNotListedException">No file of that name is listed.</exception>
    /// <exception cref="ForumFileAmbiguousException">More than one file of that name is listed.</exception>
    /// <exception cref="ForumFileSizeMismatchException">The listed file is not <paramref name="size"/> bytes.</exception>
    public async Task<Uri> Resolve(ForumSite site, long id, string fileName, long size, CancellationToken cancellationToken)
    {
        var listing = await GetFiles(site, id, cancellationToken);
        var matches = listing.Files.Where(f => string.Equals(f.Name, fileName, StringComparison.Ordinal)).ToArray();
        switch (matches.Length)
        {
            case 0: throw new ForumFileNotListedException(site.DisplayName(), fileName);
            case > 1: throw new ForumFileAmbiguousException(site.DisplayName(), fileName);
        }

        // A listing without a size says nothing either way; the hash check after the download still decides.
        if (matches[0].Size is { } listed && listed != size) throw new ForumFileSizeMismatchException(site.DisplayName(), fileName);
        return matches[0].Url;
    }

    private async Task<FileListing> Attempt(ForumSite site, long id, CancellationToken cancellationToken)
    {
        try
        {
            return await Fetch(site, id, cancellationToken);
        }
        // Asking again cannot change what the forum or the user's account has said; only a rate limit may pass.
        catch (UserFacingException e) when (e is not IRetryAfter)
        {
            throw new PermanentFailureException(e);
        }
    }

    private async Task<FileListing> Fetch(ForumSite site, long id, CancellationToken cancellationToken)
    {
        var url = new Uri(_oauth.EndpointsFor(site).ApiBase, $"api/downloads/files/{id}");
        var token = await _oauth.GetUsableToken(site, cancellationToken);
        var response = await Send(url, token, cancellationToken);
        try
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Another copy of the app may have refreshed the stored token since this one read it.
                response.Dispose();
                var cache = _credentials.For(site);
                await cache.ReloadTokenAsync(cancellationToken);
                token = await _oauth.GetUsableToken(site, cancellationToken);
                response = await Send(url, token, cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _logger.LogInformation("{Site} refused the stored token twice; signing out", site);
                    await cache.ClearAsync(cancellationToken);
                    throw new SignInAgainException(site.DisplayName());
                }
            }

            switch (response.StatusCode)
            {
                case HttpStatusCode.Forbidden:
                    throw new ForumRefusedException(site.DisplayName());
                case HttpStatusCode.TooManyRequests:
                    throw new ForumRateLimitedException(site.DisplayName(), RetryAfterOf(response));
            }

            response.EnsureSuccessStatusCode();
            return await ReadListing(site, response, cancellationToken);
        }
        finally
        {
            response.Dispose();
        }
    }

    private async Task<HttpResponseMessage> Send(Uri url, OAuthToken token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        request.Headers.UserAgent.Add(ApplicationConstants.UserAgent);
        return await _http.SendAsync(request, cancellationToken);
    }

    private TimeSpan RetryAfterOf(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var wait = header?.Delta ?? (header?.Date is { } date ? date - _time.GetUtcNow() : TimeSpan.Zero);
        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait > LongestWait ? LongestWait : wait;
    }

    private static async Task<FileListing> ReadListing(ForumSite site, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            var files = new List<ForumFile>();
            foreach (var entry in document.RootElement.GetProperty("files").EnumerateArray())
            {
                var name = entry.GetProperty("name").GetString();
                var address = entry.GetProperty("url").GetString();
                if (name is null || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                    throw new ForumReplyUnreadableException(site.DisplayName());
                long? size = entry.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var listed) ? listed : null;
                files.Add(new ForumFile(name, uri, size));
            }

            return new FileListing(files);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ForumReplyUnreadableException(site.DisplayName(), e);
        }
    }
}
