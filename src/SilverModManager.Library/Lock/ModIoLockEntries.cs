using System.Globalization;
using JetBrains.Annotations;
using SilverModManager.Library.ModIo;
using SilverModManager.Networking.ModIo;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Library.Lock;

/// <summary>
/// The mod.io subscriptions of a loadout's game, as lock entries.
/// </summary>
/// <remarks>
/// For a game whose own mod manager downloads the account's subscriptions the app owns no file, so no group of the
/// loadout links one. The entries are read from the account when the lock is exported, each naming the mod by its id and
/// carrying no hash.
/// </remarks>
[PublicAPI]
public interface IModIoLockEntries
{
    /// <summary>One entry per mod the account is subscribed to for the loadout's game; none when the game downloads its own files from mod.io or has none there.</summary>
    ValueTask<IReadOnlyList<LockEntry>> For(Loadout.ReadOnly loadout, CancellationToken cancellationToken);
}

internal sealed class ModIoLockEntries(IGameRegistry registry, IModIoApi api) : IModIoLockEntries
{
    public async ValueTask<IReadOnlyList<LockEntry>> For(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        if (!registry.TryGetGameInstallation(loadout, out var install) || ModIoGame.For(install.Game) is not { GameSyncsSubscriptions: true } support) return [];

        try
        {
            var subscribed = await api.Subscribed(support.ModIoGameId, cancellationToken);
            return [.. subscribed.Select(mod => new LockEntry(
                $"modio-{mod.Id}",
                mod.Modfile?.Version is { Length: > 0 } version ? version : ModIoFiles.UpdatedOn(mod.DateUpdated),
                mod.Id.ToString(CultureInfo.InvariantCulture),
                mod.Name,
                "",
                LockSources.ModIo))];
        }
        catch (ModIoNotSignedInException)
        {
            // No token means the app never subscribed anything for this user; there is nothing of its own to export.
            return [];
        }
        catch (Exception e) when (e is UserFacingException)
        {
            // Leaving the subscriptions out would write a lock that quietly misses mods.
            throw new LockException($"mod.io's subscriptions for {install.Game.DisplayName} could not be read, so the lock was not written: {e.Message}");
        }
    }

    /// <summary>Whether the entry names a subscription: a mod.io entry that pins no bytes.</summary>
    internal static bool IsSubscription(LockEntry entry) => entry.Source == LockSources.ModIo && string.IsNullOrEmpty(entry.Sha256);

    /// <summary>What an import tells the user about an entry: the verb that subscribes.</summary>
    internal static string SubscribeMessage(LockEntry entry)
    {
        if (!uint.TryParse(entry.FileId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            throw new LockException($"{entry.Id}: the lock names no mod.io mod id for {entry.Asset}.");

        return $"{entry.Id}: subscribe to {entry.Asset} on mod.io with `silvermodmanager modio install -l <loadout> -m {id}`; the game's Mod Manager downloads it.";
    }
}
