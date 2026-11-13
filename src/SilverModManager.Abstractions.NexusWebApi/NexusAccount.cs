using SilverModManager.Abstractions.NexusWebApi.Types;

namespace SilverModManager.Abstractions.NexusWebApi;

/// <summary>What a Nexus Mods login lets the app do: only a premium account may use the download API.</summary>
public enum NexusAccountKind
{
    SignedOut,
    Free,
    Premium,
}

public static class NexusAccount
{
    /// <summary>
    /// The account kind from <see cref="ILoginManager.GetUserInfoAsync"/>, which asks Nexus Mods when the manager holds
    /// no answer yet. The manager's <c>IsPremium</c> is filled by its own background check, which a command-line run
    /// may not have started. A failed request throws; it is not read as signed out.
    /// </summary>
    public static async ValueTask<NexusAccountKind> Current(ILoginManager login, CancellationToken token) =>
        await login.GetUserInfoAsync(token) switch
        {
            null => NexusAccountKind.SignedOut,
            { UserRole: UserRole.Premium } => NexusAccountKind.Premium,
            _ => NexusAccountKind.Free,
        };
}
