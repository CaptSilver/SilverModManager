using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using DynamicData.Kernel;
using NexusMods.Paths;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Abstractions.Library;
using SilverModManager.Library.GitHub;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.GitHub;
using SilverModManager.Networking.GitHub.DTOs;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Library.Tests.GitHub;

/// <summary>
/// An <see cref="IGitHubApi"/> that answers from tables and records what it was asked, so a test can count requests.
/// </summary>
internal sealed class FakeGitHubApi : IGitHubApi
{
    public AuthenticationHeaderValue? AuthenticationHeader { get; set; }

    /// <summary>The release GitHub marks as latest, by <c>owner/repo</c>.</summary>
    public Dictionary<string, Release> Marked { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every release of a repository, newest first, by <c>owner/repo</c>.</summary>
    public Dictionary<string, Release[]> Lists { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<ulong, Release> ById { get; } = [];
    public Dictionary<string, ulong> Ids { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Requests { get; } = [];

    /// <summary>Thrown by every request when set.</summary>
    public Exception? Failure { get; set; }

    private void Record(string request)
    {
        Requests.Add(request);
        if (Failure is not null) throw Failure;
    }

    public ValueTask<Release[]?> FetchReleases(string organization, string repository, CancellationToken cancellationToken = default)
    {
        Record($"list {organization}/{repository}");
        return ValueTask.FromResult(Lists.TryGetValue($"{organization}/{repository}", out var list) ? list : null);
    }

    public ValueTask<Release?> FetchLatestRelease(string organization, string repository, IComparer<Release>? comparer = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public ValueTask<Release?> FetchReleaseByTag(string organization, string repository, string tag, CancellationToken cancellationToken = default)
    {
        Record($"tag {organization}/{repository} {tag}");
        var release = Lists.TryGetValue($"{organization}/{repository}", out var list) ? list.FirstOrDefault(r => r.TagName == tag) : null;
        release ??= Marked.TryGetValue($"{organization}/{repository}", out var marked) && marked.TagName == tag ? marked : null;
        return ValueTask.FromResult(release);
    }

    public ValueTask<Release?> FetchMarkedLatestRelease(string organization, string repository, CancellationToken cancellationToken = default)
    {
        Record($"latest {organization}/{repository}");
        return ValueTask.FromResult(Marked.TryGetValue($"{organization}/{repository}", out var release) ? release : null);
    }

    public ValueTask<Release?> FetchLatestReleaseById(long repositoryId, CancellationToken cancellationToken = default)
    {
        Record($"latest #{repositoryId}");
        return ValueTask.FromResult(ById.TryGetValue((ulong)repositoryId, out var release) ? release : null);
    }

    public ValueTask<Release?> FetchReleaseByTagById(long repositoryId, string tag, CancellationToken cancellationToken = default)
    {
        Record($"tag #{repositoryId} {tag}");
        return ValueTask.FromResult(ById.TryGetValue((ulong)repositoryId, out var release) && release.TagName == tag ? release : null);
    }

    public ValueTask<ulong?> FetchRepositoryId(string organization, string repository, CancellationToken cancellationToken = default)
    {
        Record($"id {organization}/{repository}");
        return ValueTask.FromResult(Ids.TryGetValue($"{organization}/{repository}", out var id) ? (ulong?)id : null);
    }

    public ValueTask<bool> ValidateToken(string token, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal static class TestReleases
{
    public static Asset AssetOf(string name, string? digest = null, string? url = null) => new(
        Url: "u", Id: name.GetHashCode(), Name: name, ContentType: "application/octet-stream", State: "uploaded", Size: 10,
        DownloadCount: 7, CreatedAt: default, UpdatedAt: default, BrowserDownloadUrl: url ?? "https://example.invalid/" + name, Digest: digest);

    public static Release Make(string tag, string owner, string repository, IReadOnlyList<Asset> assets, string body = "", bool prerelease = false, DateTimeOffset? published = null)
        => new("u", "u", $"https://github.com/{owner}/{repository}/releases/tag/{tag}", tag.GetHashCode(), tag, "main", tag, false, prerelease, default, published ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), assets, "u", "u", body);

    public static Sha256Value Sha(byte[] bytes) => Sha256Value.From(SHA256.HashData(bytes));
}

/// <summary>Serves bytes by URL and counts what was fetched.</summary>
internal sealed class RoutedBytesHandler : HttpMessageHandler
{
    private readonly Dictionary<string, byte[]> _routes = [];

    public List<string> Fetched { get; } = [];

    public RoutedBytesHandler Serve(string url, byte[] bytes)
    {
        _routes[url] = bytes;
        return this;
    }

    public RoutedBytesHandler Serve(string url, string text) => Serve(url, Encoding.UTF8.GetBytes(text));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Fetched.Add(url);
        if (!_routes.TryGetValue(url, out var bytes)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return Task.FromResult(response);
    }
}

/// <summary>
/// The pieces of the GitHub shelf over one database, with a clock the test moves and an API it scripts.
/// </summary>
internal sealed class ShelfRig
{
    public FakeGitHubApi Api { get; } = new();
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
    public RoutedBytesHandler Bytes { get; } = new();
    public IGameData Game { get; }
    public GameId GameId { get; }
    public GitHubShelfEntries Entries { get; }
    public GitHubReleases Releases { get; }
    public IReadOnlyList<GitHubShelfEntryDefinition> Declared { get; }

    public ShelfRig(IConnection connection, params GitHubShelfEntryDefinition[] declared)
    {
        GameId = GameId.From("Tests.Shelf." + Guid.NewGuid().ToString("N"));
        Declared = declared;
        Game = new FakeShelfGame(GameId, declared);
        Entries = new GitHubShelfEntries(connection, [Game]);
        Releases = new GitHubReleases(Api, Entries, Time, NullLogger<GitHubReleases>.Instance);
    }

    public HttpClient Client => new(Bytes);

    public static LoadoutId SomeLoadout => LoadoutId.From(EntityId.From(0x4242));
}

/// <summary>Only what the shelf reads of a game: its id, and the repositories it declares.</summary>
internal class FakeGame(GameId id) : IGameData
{
    public GameId GameId => id;
    public StoreIdentifiers StoreIdentifiers => new(id);
    public string DisplayName => id.ToString();
    public Optional<NexusModsGameId> NexusModsGameId => Optional<NexusModsGameId>.None;
    public IStreamFactory IconImage => throw new NotSupportedException();
    public IStreamFactory TileImage => throw new NotSupportedException();
    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult) => throw new NotSupportedException();
    public GamePath GetPrimaryFile(GameInstallation installation) => throw new NotSupportedException();
}

internal sealed class FakeShelfGame(GameId id, IReadOnlyList<GitHubShelfEntryDefinition> shelf) : FakeGame(id), IGitHubShelfGame
{
    public IReadOnlyList<GitHubShelfEntryDefinition> GitHubShelf => shelf;
}
