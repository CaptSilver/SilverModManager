using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Wabbajack.Acquire.Hosts;

/// <summary>
/// Downloads a MEGA public file link. MEGA serves files encrypted: the link's key is the only thing that opens them,
/// so the job asks the API for a temporary download address, streams the bytes through AES-128-CTR and writes the
/// plain file. The list's xxHash64 check, made by the acquirer afterwards, catches a wrong key or a damaged download.
/// </summary>
public sealed record MegaDownloadJob : HostedDownloadJob, IJobDefinitionWithStart<MegaDownloadJob, AbsolutePath>
{
    public const string QuotaMessage = "MEGA's download quota is used up; try later or download in your browser";

    /// <summary>The API's single endpoint; a field so a test can point it elsewhere.</summary>
    public Uri ApiUrl { get; init; } = new("https://g.api.mega.co.nz/cs");

    protected override string HostName => "MEGA";

    public static IJobTask<MegaDownloadJob, AbsolutePath> Create(IServiceProvider provider, Uri url, string fileName, AbsolutePath destination)
    {
        var job = new MegaDownloadJob
        {
            Client = provider.GetRequiredService<HttpClient>(),
            Logger = provider.GetRequiredService<ILogger<MegaDownloadJob>>(),
            Throttle = provider.GetRequiredService<HostThrottle>(),
            Url = url,
            FileName = fileName,
            Destination = destination,
        };
        return provider.GetRequiredService<IJobMonitor>().Begin<MegaDownloadJob, AbsolutePath>(job);
    }

    public ValueTask<AbsolutePath> StartAsync(IJobContext<MegaDownloadJob> context) => RunAsync(context);

    internal override Exception ExplainStatus(HttpResponseMessage response)
        => (int)response.StatusCode == 509 ? new HostRefusedException(QuotaMessage) : base.ExplainStatus(response);

    protected override async Task Fetch(HostTransfer transfer, CancellationToken token)
    {
        if (!MegaLink.TryParse(Url, out var link))
            throw new HostRefusedException("The list's link is not a MEGA file link this app can read; download it in your browser");

        var file = await DownloadRetries.Run(Logger, $"MEGA's answer for {link!.FileId}", Attempts, RetryDelay, () => Ask(transfer, link.FileId, token), token);
        ReportedFileName = ReadName(file.Attributes, link.AesKey);

        using var request = new HttpRequestMessage(HttpMethod.Get, file.DownloadUrl);
        using var response = await transfer.Send(request, token);
        await transfer.Save(response, file.Size, network => new Aes128CtrStream(network, link.AesKey, link.Nonce), HostName);
    }

    private async Task<MegaFile> Ask(HostTransfer transfer, string fileId, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}?id={Random.Shared.Next()}")
        {
            Content = new StringContent($"[{{\"a\":\"g\",\"g\":1,\"p\":\"{fileId}\"}}]", Encoding.UTF8, "application/json"),
        };
        using var response = await transfer.Send(request, token);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);

        var answer = document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0 ? document.RootElement[0] : document.RootElement;
        if (answer.ValueKind == JsonValueKind.Number) throw ForCode(answer.GetInt32());
        if (answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("e", out var error) && error.ValueKind == JsonValueKind.Number) throw ForCode(error.GetInt32());
        if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("g", out var address) || address.GetString() is not { } url || !answer.TryGetProperty("s", out var size))
            throw new PermanentFailureException(new HostRefusedException("MEGA's answer did not include the file; download it in your browser"));

        return new MegaFile(new Uri(url), size.GetInt64(), answer.TryGetProperty("at", out var attributes) ? attributes.GetString() : null);
    }

    /// <summary>
    /// Error numbers from MEGA's API. The busy ones are worth asking again; the rest will answer the same next time.
    /// </summary>
    private static Exception ForCode(int code) => code switch
    {
        -3 or -4 or -18 or -19 => new HostRefusedException("MEGA is temporarily unavailable; try again later or download in your browser"),
        -17 => new PermanentFailureException(new HostRefusedException(QuotaMessage)),
        -9 or -8 => new PermanentFailureException(new HostRefusedException("MEGA has no file at this link any more; download it in your browser if you have another")),
        -16 => new PermanentFailureException(new HostRefusedException("MEGA took this file down; nothing to download")),
        _ => new PermanentFailureException(new HostRefusedException($"MEGA refused the link (error {code}); download it in your browser")),
    };

    /// <summary>The file's name from its attributes: JSON prefixed with "MEGA", encrypted with AES-CBC and the file key.</summary>
    private static string? ReadName(string? attributes, byte[] aesKey)
    {
        try
        {
            if (attributes is null || !MegaLink.TryDecode(attributes, out var encrypted) || encrypted.Length == 0 || encrypted.Length % 16 != 0) return null;
            using var aes = Aes.Create();
            aes.Key = aesKey;
            var plain = aes.DecryptCbc(encrypted, new byte[16], PaddingMode.None);
            var text = Encoding.UTF8.GetString(plain).TrimEnd('\0');
            if (!text.StartsWith("MEGA", StringComparison.Ordinal)) return null;
            using var document = JsonDocument.Parse(text[4..]);
            return document.RootElement.TryGetProperty("n", out var name) ? name.GetString() : null;
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    private sealed record MegaFile(Uri DownloadUrl, long Size, string? Attributes);
}
