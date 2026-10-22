using NexusMods.Games.FromSoft.Build;

namespace NexusMods.Games.FromSoft.Heal;

/// <summary>
/// The sentence for an install whose regulation changed without the game being patched. One wording for every place
/// that refuses on it, so the apply and Health Check say the same thing.
/// </summary>
public static class TamperedRegulation
{
    public static string Describe(string regulationName, BuildIdentity live)
        => $"{regulationName} changed but the game was not patched (exe {live.Exe}, build id {live.SteamBuildId} unchanged) — "
           + "something overwrote the install's regulation. Refusing to adopt it as vanilla. "
           + "Run Steam → Verify integrity of game files, or heal against the last known-good baseline.";
}
