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

            var files = new List<StagedFile>();
            if (prelude.Body is not null) files.Add(new StagedFile(PreludePath, prelude.Body, prelude.ETag));
            if (masterlist.Body is not null) files.Add(new StagedFile(MasterlistPath(repo), masterlist.Body, masterlist.ETag));
            ReplaceTogether(files);
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

    private readonly record struct StagedFile(string Path, string Body, string? ETag);

    /// <summary>
    /// Replaces the cache files as one unit. Every new file is written to a temp name first, so a
    /// failed write touches nothing; if a later move fails, the files already replaced are put back,
    /// so <see cref="Current"/> never pairs a masterlist with a prelude it wasn't validated with.
    /// </summary>
    private static void ReplaceTogether(List<StagedFile> files)
    {
        // (target, temp content to move in or null to delete the target)
        var steps = new List<(string Target, string? Temp)>();
        var applied = new List<(string Target, bool BackedUp)>();
        try
        {
            foreach (var file in files)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file.Path) ?? throw new ArgumentException($"\"{file.Path}\" has no parent directory", nameof(files)));
                IoFile.WriteAllText(file.Path + ".tmp", file.Body);
                steps.Add((file.Path, file.Path + ".tmp"));
                if (file.ETag is not null)
                {
                    IoFile.WriteAllText(file.Path + ".etag.tmp", file.ETag);
                    steps.Add((file.Path + ".etag", file.Path + ".etag.tmp"));
                }
                else
                {
                    steps.Add((file.Path + ".etag", null));
                }
            }

            foreach (var (target, temp) in steps)
            {
                // Set the old file aside first so a later failure can bring it back.
                var backedUp = IoFile.Exists(target);
                if (backedUp) IoFile.Move(target, target + ".bak", overwrite: true);
                applied.Add((target, backedUp));
                if (temp is not null) IoFile.Move(temp, target, overwrite: true);
            }
        }
        catch
        {
            for (var i = applied.Count - 1; i >= 0; i--)
            {
                // Only a backup this call made goes back; a .bak left by a crashed refresh is stale.
                var (target, backedUp) = applied[i];
                if (backedUp) IoFile.Move(target + ".bak", target, overwrite: true);
                else if (IoFile.Exists(target)) IoFile.Delete(target);
            }

            foreach (var (_, temp) in steps)
            {
                if (temp is not null) IoFile.Delete(temp);
            }
            throw;
        }

        foreach (var (target, _) in applied) IoFile.Delete(target + ".bak");
    }

    private static string? ReadIfPresent(string path) => IoFile.Exists(path) ? IoFile.ReadAllText(path) : null;

    // The lock records a calendar day, not a time, so a copy downloaded on that day counts as current.
    private static string? ReadIfNewer(string path, DateTimeOffset bundledOn) =>
        IoFile.Exists(path) && IoFile.GetLastWriteTimeUtc(path) >= bundledOn.UtcDateTime ? IoFile.ReadAllText(path) : null;
}
