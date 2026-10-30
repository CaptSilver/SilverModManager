using System.Text;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Wabbajack.Acquire.Hosts;

/// <summary>
/// A download the app cannot complete and the user can: its message is what the plan shows beside the archive, so it
/// says what happened and what to do instead.
/// </summary>
public sealed class HostRefusedException(string message, Exception? inner = null) : UserFacingException(message, inner)
{
    public override string Title => "Download not completed";
}

/// <summary>
/// The shared shape of a download from one file host. The base takes a slot from the <see cref="HostThrottle"/>,
/// removes a partly written file when anything goes wrong, and records the library file's name and download page. A
/// host's job says only how to get from the list's address to bytes in <see cref="Destination"/>.
/// </summary>
public abstract record HostedDownloadJob : IDownloadJob
{
    public required HttpClient Client { get; init; }
    public required ILogger Logger { get; init; }
    public required HostThrottle Throttle { get; init; }

    /// <summary>The address the list gives; recorded as the download page too.</summary>
    public required Uri Url { get; init; }

    public required AbsolutePath Destination { get; init; }

    /// <summary>The archive's name in the list, used for the library file when the host does not say better.</summary>
    public string? FileName { get; init; }

    /// <summary>How many times a flaky request is made before the download is given up.</summary>
    public int Attempts { get; init; } = 6;

    /// <summary>The pause before the first retry; each one after waits twice as long as the last.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a body may send nothing before the host is treated as dead and the download fails.</summary>
    public TimeSpan IdleTimeout { get; init; } = IdleReads.DefaultTimeout;

    /// <summary>The clock the idle limit runs on.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>The host's name as the user knows it. Also the throttle's key, so every job for the host shares its slots.</summary>
    protected abstract string HostName { get; }

    /// <summary>A file name the host reported, preferred over <see cref="FileName"/>.</summary>
    protected string? ReportedFileName { get; set; }

    protected abstract Task Fetch(HostTransfer transfer, CancellationToken token);

    /// <summary>The reason shown to the user for a non-success answer; a host overrides it for the codes it uses for its own limits.</summary>
    internal virtual Exception ExplainStatus(HttpResponseMessage response)
        => new HostRefusedException($"{HostName} answered {(int)response.StatusCode}; download it in your browser");

    protected async ValueTask<AbsolutePath> RunAsync(IJobContext context)
    {
        await context.YieldAsync();
        var token = context.CancellationToken;
        using var slot = await Throttle.Enter(HostName, token);
        try
        {
            await Fetch(new HostTransfer(this, context, HostName), token);
        }
        catch
        {
            RemovePartialFile();
            throw;
        }
        return Destination;
    }

    private void RemovePartialFile()
    {
        try
        {
            if (Destination.FileExists) Destination.Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning(e, "Could not remove the partly downloaded file {Path}", Destination);
        }
    }

    /// <inheritdoc/>
    public ValueTask AddMetadata(ITransaction transaction, LibraryFile.New libraryFile)
    {
        if ((ReportedFileName ?? FileName) is { } name)
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
}

/// <summary>What a host's job does with the network and the destination file: send a request, write a response to disk.</summary>
public sealed class HostTransfer
{
    /// <summary>Large reads keep the write loop out of the way of the network; the file is hashed later in the same block size.</summary>
    private const int BlockSize = HashingLimits.ReadBlockSize;

    private readonly HostedDownloadJob _job;
    private readonly IJobContext _context;
    private readonly string _hostName;

    internal HostTransfer(HostedDownloadJob job, IJobContext context, string hostName)
    {
        _job = job;
        _context = context;
        _hostName = hostName;
    }

    public HttpClient Client => _job.Client;

    /// <summary>Sends the request and returns once the headers are in, so a large body is not buffered.</summary>
    public Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken token)
        => Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

    /// <summary>The page at the address as text; used to find the real link on a host's landing page.</summary>
    public async Task<string> GetPage(Uri url, CancellationToken token, IEnumerable<KeyValuePair<string, string>>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in headers ?? []) request.Headers.TryAddWithoutValidation(name, value);
        using var response = await Send(request, token);
        EnsureSuccess(response);
        return await ReadText(response, token);
    }

    /// <summary>
    /// The whole body as text, read through the idle limit. <c>ReadAsStringAsync</c> would wait forever on a
    /// connection that dies after the headers, and <c>ResponseHeadersRead</c> leaves the body outside HttpClient's timeout.
    /// </summary>
    public async Task<string> ReadText(HttpResponseMessage response, CancellationToken token)
    {
        var bytes = await ReadBytes(response, token);
        var charset = response.Content.Headers.ContentType?.CharSet;
        var encoding = Encoding.UTF8;
        if (charset is not null)
        {
            try { encoding = Encoding.GetEncoding(charset.Trim('"')); }
            catch (ArgumentException) { /* unknown label: fall back to UTF-8, like HttpContent does */ }
        }
        using var reader = new StreamReader(new MemoryStream(bytes), encoding, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(token);
    }

    /// <summary>The whole body in memory, read through the idle limit; for small pages and API answers.</summary>
    public async Task<byte[]> ReadBytes(HttpResponseMessage response, CancellationToken token)
    {
        await using var network = await response.Content.ReadAsStreamAsync(token);
        return await IdleReads.ReadToEndAsync(network, _hostName, _job.IdleTimeout, _job.TimeProvider, token);
    }

    /// <summary>Throws the job's reason for a failure status; a success passes.</summary>
    public void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw _job.ExplainStatus(response);
    }

    public static bool IsWebPage(HttpResponseMessage response)
        => response.Content.Headers.ContentType?.MediaType is "text/html" or "application/xhtml+xml";

    /// <summary>
    /// Writes the response body to the destination in 1 MB blocks, reporting progress. A body that sends nothing for
    /// <see cref="HostedDownloadJob.IdleTimeout"/> fails the transfer with a <see cref="DownloadStalledException"/>. <paramref name="decrypt"/> wraps
    /// the network stream for hosts that encrypt what they serve. A web page, a failure status or a body that is not
    /// <paramref name="expectedSize"/> bytes fails the transfer.
    /// </summary>
    public async Task Save(HttpResponseMessage response, long? expectedSize = null, Func<Stream, Stream>? decrypt = null, string? hostName = null)
    {
        var token = _context.CancellationToken;
        EnsureSuccess(response);
        var host = hostName ?? response.RequestMessage?.RequestUri?.Host ?? _job.Url.Host;
        if (IsWebPage(response))
            throw new HostRefusedException($"{host} sent a web page instead of the file; download it in your browser");

        var total = expectedSize ?? response.Content.Headers.ContentLength;
        await using var network = await response.Content.ReadAsStreamAsync(token);
        await using var source = decrypt?.Invoke(network) ?? network;
        long written;
        await using (var file = _job.Destination.Open(FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = GC.AllocateUninitializedArray<byte>(BlockSize);
            written = 0;
            int read;
            while ((read = await IdleReads.ReadAsync(source, buffer, host, _job.IdleTimeout, _job.TimeProvider, token)) > 0)
            {
                await _context.YieldAsync();
                await file.WriteAsync(buffer.AsMemory(0, read), token);
                written += read;
                if (total is > 0) _context.SetPercent(Size.FromLong(Math.Min(written, total.Value)), Size.FromLong(total.Value));
            }
            await file.FlushAsync(token);
        }

        if (total is { } expected && written != expected)
            throw new InvalidDataException($"The host sent {written} bytes of a file that should be {expected} bytes");
    }
}
