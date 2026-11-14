using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Networking.GitHub;
using SilverModManager.Networking.GitHub.DTOs;

namespace SilverModManager.Library.GitHub;

/// <summary>
/// <see cref="IGitHubApi"/> that looks the real one up when a request is made. The library registers the shelf, the
/// mod source and the verbs in every host that has a library, including hosts that never added the GitHub client; those
/// can still list, export and verify, and only a request that needs GitHub finds it missing.
/// </summary>
internal sealed class DeferredGitHubApi(IServiceProvider provider) : IGitHubApi
{
    private IGitHubApi Api => provider.GetRequiredService<IGitHubApi>();

    public AuthenticationHeaderValue? AuthenticationHeader
    {
        get => Api.AuthenticationHeader;
        set => Api.AuthenticationHeader = value;
    }

    public ValueTask<Release[]?> FetchReleases(string organization, string repository, CancellationToken cancellationToken = default)
        => Api.FetchReleases(organization, repository, cancellationToken);

    public ValueTask<Release?> FetchLatestRelease(string organization, string repository, IComparer<Release>? comparer = null, CancellationToken cancellationToken = default)
        => Api.FetchLatestRelease(organization, repository, comparer, cancellationToken);

    public ValueTask<Release?> FetchReleaseByTag(string organization, string repository, string tag, CancellationToken cancellationToken = default)
        => Api.FetchReleaseByTag(organization, repository, tag, cancellationToken);

    public ValueTask<Release?> FetchMarkedLatestRelease(string organization, string repository, CancellationToken cancellationToken = default)
        => Api.FetchMarkedLatestRelease(organization, repository, cancellationToken);

    public ValueTask<Release?> FetchLatestReleaseById(long repositoryId, CancellationToken cancellationToken = default)
        => Api.FetchLatestReleaseById(repositoryId, cancellationToken);

    public ValueTask<Release?> FetchReleaseByTagById(long repositoryId, string tag, CancellationToken cancellationToken = default)
        => Api.FetchReleaseByTagById(repositoryId, tag, cancellationToken);

    public ValueTask<ulong?> FetchRepositoryId(string organization, string repository, CancellationToken cancellationToken = default)
        => Api.FetchRepositoryId(organization, repository, cancellationToken);

    public ValueTask<bool> ValidateToken(string token, CancellationToken cancellationToken = default)
        => Api.ValidateToken(token, cancellationToken);
}

/// <summary>
/// The GitHub client and HTTP client the shelf's services are built with, looked up when they are first used.
/// </summary>
internal static class GitHubClients
{
    public static IGitHubApi Api(IServiceProvider provider) => new DeferredGitHubApi(provider);

    public static Func<HttpClient> Http(IServiceProvider provider) => () => provider.GetRequiredService<HttpClient>();
}
