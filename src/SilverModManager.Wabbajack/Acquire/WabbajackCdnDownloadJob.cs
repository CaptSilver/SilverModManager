// Follows Wabbajack.Downloaders.WabbajackCDN/WabbajackCDNDownloader.cs.
using SilverModManager.Networking.HttpDownloader;
using System.IO.Compression;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Downloads;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Format;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>
/// Downloads a file from the Wabbajack CDN: a gzipped definition names the file's parts, each part is fetched and
/// checked on its own, and the assembled file is checked against the definition. A plain GET of the CDN address
/// returns a small HTML page, not the file, so the generic HTTP job cannot do this.
/// </summary>
public sealed record WabbajackCdnDownloadJob : IJobDefinitionWithStart<WabbajackCdnDownloadJob, AbsolutePath>, IDownloadJob
{
    private const int ParallelParts = 4;

    /// <summary>
    /// Older lists carry Bunny CDN hosts; the same files are served from these.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DomainRemaps = new Dictionary<string, string>
    {
        ["wabbajack.b-cdn.net"] = "authored-files.wabbajack.org",
        ["wabbajack-mirror.b-cdn.net"] = "mirror.wabbajack.org",
        ["wabbajack-patches.b-cdn.net"] = "patches.wabbajack.org",
        ["wabbajacktest.b-cdn.net"] = "test-files.wabbajack.org",
    };

    public required HttpClient Client { get; init; }
    public required ILogger Logger { get; init; }

    /// <summary>The address the list gives; also recorded as the download page.</summary>
    public required Uri Url { get; init; }

    public required AbsolutePath Destination { get; init; }

    /// <summary>How many times a part or the definition is asked for before the download is given up.</summary>
    public int Attempts { get; init; } = 6;

    /// <summary>The pause before the first retry; each one after waits twice as long as the last.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a response may send nothing before the request counts as failed and is asked for again.</summary>
    public TimeSpan IdleTimeout { get; init; } = IdleReads.DefaultTimeout;

    /// <summary>The clock the idle limit runs on.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    private string? _originalFileName;

    public static IJobTask<WabbajackCdnDownloadJob, AbsolutePath> Create(IServiceProvider provider, Uri cdnUrl, AbsolutePath destination)
    {
        var job = new WabbajackCdnDownloadJob
        {
            Client = provider.GetRequiredService<HttpClient>(),
            Logger = provider.GetRequiredService<ILogger<WabbajackCdnDownloadJob>>(),
            Url = cdnUrl,
            Destination = destination,
        };
        return provider.GetRequiredService<IJobMonitor>().Begin<WabbajackCdnDownloadJob, AbsolutePath>(job);
    }

    /// <summary>
    /// A GET for the address, sent to the host that now serves it and naming that host in the <c>Host</c> header,
    /// which the CDN's front end routes on.
    /// </summary>
    public static HttpRequestMessage MakeMessage(Uri url)
    {
        string host;
        if (DomainRemaps.TryGetValue(url.Host, out var remapped))
        {
            url = new UriBuilder(url) { Host = remapped }.Uri;
            host = remapped;
        }
        else
        {
            host = url.Host;
        }

        var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Headers.Host = host;
        return message;
    }

    public async ValueTask<AbsolutePath> StartAsync(IJobContext<WabbajackCdnDownloadJob> context)
    {
        await context.YieldAsync();
        var token = context.CancellationToken;

        var definition = await WithRetries($"the definition of {Url}", async () =>
        {
            using var message = MakeMessage(new Uri($"{Url.AbsoluteUri.TrimEnd('/')}/definition.json.gz"));
            using var response = await Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
            // Gone is gone; anything else (a busy CDN, a 5xx, a dropped connection) is worth asking again.
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new PermanentFailureException(new HttpRequestException($"The CDN has no file at {Url}", null, HttpStatusCode.NotFound));
            response.EnsureSuccessStatusCode();
            return await ReadDefinition(response, token);
        }, token);

        _originalFileName = definition.OriginalFileName;

        await using (var file = Destination.Open(FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            file.SetLength(definition.Size);
            long written = 0;

            using var gate = new SemaphoreSlim(ParallelParts);
            using var writeLock = new SemaphoreSlim(1);

            // The first part to fail for good stops the rest; otherwise they would all download before the job
            // reported the failure.
            using var partsCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var partToken = partsCts.Token;
            Exception? firstFailure = null;

            try
            {
                await Task.WhenAll(definition.Parts.Select(async part =>
                {
                    try
                    {
                        await gate.WaitAsync(partToken);
                        try
                        {
                            // A slot can free up in the same moment the cancel lands.
                            partToken.ThrowIfCancellationRequested();
                            var bytes = await FetchPart(definition, part, partToken);
                            // One stream, so writes take turns; fetching is what runs in parallel.
                            await writeLock.WaitAsync(partToken);
                            try
                            {
                                file.Position = part.Offset;
                                await file.WriteAsync(bytes, partToken);
                            }
                            finally
                            {
                                writeLock.Release();
                            }
                            context.SetPercent(Size.FromLong(Interlocked.Add(ref written, bytes.Length)), Size.FromLong(Math.Max(1, definition.Size)));
                        }
                        finally
                        {
                            gate.Release();
                        }
                    }
                    catch (Exception e) when (!(e is OperationCanceledException && partToken.IsCancellationRequested))
                    {
                        Interlocked.CompareExchange(ref firstFailure, e, null);
                        partsCts.Cancel();
                        throw;
                    }
                }));
            }
            catch when (firstFailure is not null)
            {
                // The siblings' cancellations can sort ahead of the real error in WhenAll; report the real one.
                ExceptionDispatchInfo.Capture(firstFailure).Throw();
            }
            await file.FlushAsync(token);
        }

        var onDisk = Destination.FileInfo.Size.Value;
        if (onDisk != (ulong)definition.Size)
            throw new InvalidDataException($"{definition.OriginalFileName} came to {onDisk} bytes, the CDN says {definition.Size}");

        var hash = FileIdentity.XxHash64(await FileHashing.XxHash64(Destination, token));
        if (!hash.Equals(definition.Hash))
            throw new InvalidDataException($"{definition.OriginalFileName} does not match the hash the CDN gives for it");

        return Destination;
    }

    private Task<byte[]> FetchPart(FileDefinition definition, PartDefinition part, CancellationToken token)
    {
        return WithRetries($"part {part.Index} of {definition.OriginalFileName}", async () =>
        {
            using var message = MakeMessage(new Uri($"{Url.AbsoluteUri.TrimEnd('/')}/parts/{part.Index}"));
            using var response = await Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidDataException($"Bad response ({(int)response.StatusCode}) for part {part.Index} of {definition.OriginalFileName}");

            await using var body = await response.Content.ReadAsStreamAsync(token);
            var bytes = await IdleReads.ReadToEndAsync(body, Url.Host, IdleTimeout, TimeProvider, token);
            if (bytes.Length != part.Size || !FileIdentity.XxHash64(Xx64Hasher.Hash(bytes)).Equals(part.Hash))
                throw new InvalidDataException($"Invalid hash for part {part.Index} of {definition.OriginalFileName}");
            return bytes;
        }, token);
    }

    private async Task<FileDefinition> ReadDefinition(HttpResponseMessage response, CancellationToken token)
    {
        await using var network = await response.Content.ReadAsStreamAsync(token);
        await using var stream = new MemoryStream(await IdleReads.ReadToEndAsync(network, Url.Host, IdleTimeout, TimeProvider, token));
        await using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var document = await JsonDocument.ParseAsync(gzip, cancellationToken: token);
        return FileDefinition.Read(document.RootElement);
    }

    /// <summary>
    /// A CDN file is a definition and hundreds of parts, so a large one makes hundreds of requests and a brief outage
    /// lands on some of them. Each request is retried with a growing pause rather than failing the whole download over
    /// a hiccup. A file the CDN does not have is not retried, and nor is the user cancelling.
    /// </summary>
    private Task<T> WithRetries<T>(string what, Func<Task<T>> request, CancellationToken token)
        => DownloadRetries.Run(Logger, what, Attempts, RetryDelay, request, token);

    /// <inheritdoc/>
    public ValueTask AddMetadata(ITransaction transaction, LibraryFile.New libraryFile)
    {
        if (_originalFileName is { } name)
        {
            var fileName = RelativePath.FromUnsanitizedInput(name).FileName;
            libraryFile.FileName = fileName;
            libraryFile.GetLibraryItem(transaction).Name = fileName.ToString();
        }

        _ = new DownloadedFile.New(transaction, libraryFile.Id)
        {
            DownloadPageUri = Url,
            LibraryFile = libraryFile,
        };
        return ValueTask.CompletedTask;
    }

    private sealed record FileDefinition(string OriginalFileName, long Size, FileIdentity Hash, IReadOnlyList<PartDefinition> Parts)
    {
        public static FileDefinition Read(JsonElement root)
        {
            var parts = root.GetProperty("Parts").EnumerateArray().Select(p => new PartDefinition(
                p.GetProperty("Index").GetInt64(),
                p.GetProperty("Offset").GetInt64(),
                p.GetProperty("Size").GetInt64(),
                WabbajackHash.Parse(RequiredString(p, "Hash")))).ToArray();
            return new FileDefinition(
                RequiredString(root, "OriginalFileName"),
                root.GetProperty("Size").GetInt64(),
                WabbajackHash.Parse(RequiredString(root, "Hash")),
                parts);
        }

        private static string RequiredString(JsonElement element, string name)
            => element.GetProperty(name).GetString() ?? throw new InvalidDataException($"The CDN's file definition has no {name}");
    }

    private sealed record PartDefinition(long Index, long Offset, long Size, FileIdentity Hash);
}
