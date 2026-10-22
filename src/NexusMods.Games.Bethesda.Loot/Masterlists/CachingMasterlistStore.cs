using System.Net;
using System.Net.Http.Headers;
using NexusMods.Games.Bethesda.Loot.Metadata;
using NexusMods.Paths;
using IoFile = System.IO.File;

namespace NexusMods.Games.Bethesda.Loot.Masterlists;

public enum MasterlistRefreshResult
{
    /// <summary>The server had nothing newer than the cached copy.</summary>
    NotModified,

    /// <summary>A newer copy passed validation and replaced the cache.</summary>
    Updated,

    /// <summary>The request or validation failed; the previous copy is untouched.</summary>
    Failed,
}

/// <summary>
/// Prefers a downloaded copy under &lt;dataDir&gt;/loot over the bundled one when it is newer than
/// the bundle's lock date. The store never schedules a refresh itself; the caller decides when
/// <see cref="RefreshAsync"/> runs.
/// </summary>
public sealed class CachingMasterlistStore(AbsolutePath dataDir, BundledMasterlistStore bundled) : IMasterlistStore
{
    /// <summary>
    /// Where refreshed masterlists come from: the LOOT organization's repositories on GitHub.
    /// </summary>
    public const string Endpoint = "https://raw.githubusercontent.com/loot/";

    private const string Branch = "v0.29";
    private const string PreludeRepository = "prelude";

    private string LootDir => dataDir.Combine("loot").ToString();
    private string MasterlistPath(string repo) => Path.Combine(LootDir, repo, "masterlist.yaml");
    private string PreludePath => Path.Combine(LootDir, "prelude.yaml");

    public MasterlistContent Current(LootGame game)
    {
        var repo = game.MasterlistRepository();

        var cached = ReadIfNewer(MasterlistPath(repo), bundled.RetrievedOn(repo));
        var masterlist = cached ?? bundled.MasterlistText(repo);
        var prelude = ReadIfNewer(PreludePath, bundled.RetrievedOn(PreludeRepository)) ?? bundled.PreludeText();
        var revision = cached is null
            ? bundled.BundledRevision(repo)
            : $"{repo} (downloaded {IoFile.GetLastWriteTimeUtc(MasterlistPath(repo)):yyyy-MM-dd})";
        return new MasterlistContent(masterlist, prelude, revision);
    }

    /// <summary>
    /// Conditionally downloads the masterlist for <paramref name="game"/> and the prelude. A new copy
    /// replaces the cache only if the masterlist still loads with the prelude it will be used with;
    /// any HTTP error, network failure or parse error leaves the previous copy in place.
    /// </summary>
    public async Task<MasterlistRefreshResult> RefreshAsync(LootGame game, HttpClient client, CancellationToken cancellationToken)
    {
        var repo = game.MasterlistRepository();
        var current = Current(game);

        try
        {
            var prelude = await FetchAsync(client, PreludeRepository, "prelude.yaml", PreludePath, cancellationToken);
            var masterlist = await FetchAsync(client, repo, "masterlist.yaml", MasterlistPath(repo), cancellationToken);
            if (prelude.Failed || masterlist.Failed) return MasterlistRefreshResult.Failed;
            if (prelude.Body is null && masterlist.Body is null) return MasterlistRefreshResult.NotModified;

            // Load the pair that would be in effect afterwards: a new prelude can break an old
            // masterlist as easily as the reverse.
            MetadataDocument.LoadWithPrelude(masterlist.Body ?? current.Masterlist, prelude.Body ?? current.Prelude);

            if (prelude.Body is not null) Store(PreludePath, prelude.Body, prelude.ETag);
            if (masterlist.Body is not null) Store(MasterlistPath(repo), masterlist.Body, masterlist.ETag);
            return MasterlistRefreshResult.Updated;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException
                                      or MetadataLoadException or MetadataParseException or YamlMergeKeyException
                                      or InvalidPluginNameException or UnauthorizedAccessException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return MasterlistRefreshResult.Failed;
        }
    }

    private readonly record struct Fetched(string? Body, string? ETag, bool Failed);

    private static async Task<Fetched> FetchAsync(HttpClient client, string repo, string file, string cachePath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Endpoint}{repo}/{Branch}/{file}");

        // An ETag only describes the cache file it was saved with.
        if (IoFile.Exists(cachePath) && ReadIfPresent(cachePath + ".etag") is { } etag && EntityTagHeaderValue.TryParse(etag, out var parsed))
            request.Headers.IfNoneMatch.Add(parsed);

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified) return new Fetched(null, null, false);
        if (response.StatusCode != HttpStatusCode.OK) return new Fetched(null, null, true);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new Fetched(body, response.Headers.ETag?.ToString(), false);
    }

    private static void Store(string path, string body, string? etag)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new ArgumentException($"\"{path}\" has no parent directory", nameof(path)));
        WriteAtomically(path, body);
        if (etag is not null) WriteAtomically(path + ".etag", etag);
        else IoFile.Delete(path + ".etag");
    }

    private static void WriteAtomically(string path, string text)
    {
        var temp = path + ".tmp";
        IoFile.WriteAllText(temp, text);
        IoFile.Move(temp, path, overwrite: true);
    }

    private static string? ReadIfPresent(string path) => IoFile.Exists(path) ? IoFile.ReadAllText(path) : null;

    // The lock records a calendar day, not a time, so a copy downloaded on that day counts as current.
    private static string? ReadIfNewer(string path, DateTimeOffset bundledOn) =>
        IoFile.Exists(path) && IoFile.GetLastWriteTimeUtc(path) >= bundledOn.UtcDateTime ? IoFile.ReadAllText(path) : null;
}
