namespace NexusMods.Abstractions.NexusWebApi;

/// <summary>
/// Generic interface for creating <see cref="HttpRequestMessage"/>s for Nexus requests, will append
/// headers to the request as required to authenticate with the Nexus API
/// </summary>
public interface IHttpMessageFactory
{
    /// <summary>
    /// Creates a new <see cref="HttpRequestMessage"/> for the given <paramref name="method"/> and <paramref name="uri"/>
    /// </summary>
    public ValueTask<HttpRequestMessage> Create(HttpMethod method, Uri uri);

    /// <summary>
    /// Adds the current credential to an outgoing request. When <see cref="IsAuthenticated"/> is
    /// false this is a pass-through: the request goes out anonymous, as public endpoints allow.
    /// </summary>
    ValueTask ApplyAuthentication(HttpRequestMessage request, CancellationToken cancellationToken);

    /// <summary>
    /// Returns true if a usable credential (an API key or an OAuth token) is available; else false.
    /// </summary>
    public ValueTask<bool> IsAuthenticated();
}
