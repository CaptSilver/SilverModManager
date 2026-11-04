using SilverModManager.Sdk.Errors;

namespace SilverModManager.Networking.NexusWebApi;

/// <summary>
/// Nexus Mods answered a typed API key with 401 or 403, so the key was not stored.
/// </summary>
public sealed class NexusApiKeyRejectedException()
    : UserFacingException("Nexus Mods did not accept this API key. Check that it was copied completely; nothing was stored.")
{
    public override string Title => "The API key was not accepted";
}
