namespace SilverModManager.Sdk.Errors;

/// <summary>
/// A download host kept the connection open but sent nothing for longer than the idle limit. Raised by
/// <see cref="IO.IdleReads"/>; a job that can resume asks again, the rest let it fail so the row shows the reason.
/// </summary>
public sealed class DownloadStalledException(string host) : UserFacingException($"{host} stopped sending data")
{
    public override string Title => "The download stalled";

    /// <summary>The host (and port, when not the default) that went quiet.</summary>
    public string Host { get; } = host;
}
