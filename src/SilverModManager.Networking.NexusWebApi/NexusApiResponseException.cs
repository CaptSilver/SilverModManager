using System.Text.Json;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Networking.NexusWebApi;

/// <summary>
/// The Nexus Mods API answered, but the reply is missing something the app needs or has the wrong shape.
/// The message names the request and the JSON path that failed.
/// </summary>
public sealed class NexusApiResponseException : UserFacingException
{
    /// <summary>
    /// Creates the refusal for the request to <paramref name="requestUri"/> whose reply could not be read.
    /// </summary>
    public NexusApiResponseException(Uri? requestUri, JsonException inner)
        : base($"Nexus Mods sent a reply to {requestUri} that the app cannot read: {inner.Message}", inner)
    {
    }

    /// <inheritdoc />
    public override string Title => "Nexus Mods sent an unexpected reply";
}
