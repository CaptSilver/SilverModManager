using System.Reactive;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using SilverModManager.Networking.GameBanana;
using SilverModManager.Networking.GameBanana.DTOs;

namespace SilverModManager.Library.GameBanana;

/// <summary>
/// The newest page of each mod the library holds a file of, kept for an hour so that entering a page and the update
/// check do not ask GameBanana about forty mods every time.
/// </summary>
internal sealed class GameBananaProfiles(IGameBananaApi api, TimeProvider time, ILogger<GameBananaProfiles> logger)
{
    internal static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    private readonly Dictionary<long, (DateTimeOffset At, GameBananaProfile? Profile)> _kept = [];
    private readonly System.Threading.Lock _gate = new();
    private readonly Subject<Unit> _changed = new();

    /// <summary>Emits when an answer was added or the user's choices that depend on the answers changed.</summary>
    public IObservable<Unit> Changed => _changed;

    public void NotifyChanged() => _changed.OnNext(Unit.Default);

    /// <summary>
    /// The mod's page, from the copy kept within the hour unless <paramref name="force"/>. Null when the mod is private,
    /// withheld or gone; that answer is kept too, so a mod that went away is not asked about again every time.
    /// </summary>
    public async Task<GameBananaProfile?> Get(long modId, bool force, CancellationToken cancellationToken)
    {
        if (!force && Fresh(modId, out var kept)) return kept;

        GameBananaProfile? profile;
        try
        {
            profile = await api.Profile(modId, cancellationToken);
        }
        catch (GameBananaUnavailableException e)
        {
            logger.LogInformation("GameBanana mod {ModId} is not available: {Reason}", modId, e.Message);
            profile = null;
        }

        lock (_gate) _kept[modId] = (time.GetUtcNow(), profile);
        return profile;
    }

    /// <summary>
    /// What was last learned about the mod, or null when nothing was or the mod is not available.
    /// </summary>
    public GameBananaProfile? Peek(long modId)
    {
        lock (_gate) return _kept.TryGetValue(modId, out var entry) ? entry.Profile : null;
    }

    private bool Fresh(long modId, out GameBananaProfile? profile)
    {
        lock (_gate)
        {
            if (_kept.TryGetValue(modId, out var entry) && time.GetUtcNow() - entry.At < MaxAge)
            {
                profile = entry.Profile;
                return true;
            }
        }

        profile = null;
        return false;
    }
}
