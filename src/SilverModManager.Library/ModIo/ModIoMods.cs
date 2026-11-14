using System.Reactive;
using System.Reactive.Subjects;
using SilverModManager.Networking.ModIo;

namespace SilverModManager.Library.ModIo;

/// <summary>
/// The newest state of each mod the library holds a file of, kept for an hour so that entering a page and the update
/// check do not ask mod.io about every mod each time. mod.io asks callers to stay under a request budget, and going
/// over it can cost the token.
/// </summary>
internal sealed class ModIoMods(IModIoApi api, TimeProvider time)
{
    internal static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    private readonly Dictionary<(uint Game, uint Mod), (DateTimeOffset At, ModIoMod? Mod)> _kept = [];
    private readonly System.Threading.Lock _gate = new();
    private readonly Subject<Unit> _changed = new();

    /// <summary>Emits when an answer was added or the user's choices that depend on the answers changed.</summary>
    public IObservable<Unit> Changed => _changed;

    public void NotifyChanged() => _changed.OnNext(Unit.Default);

    /// <summary>
    /// The mod, from the copy kept within the hour unless <paramref name="force"/>. Null when the game has no such mod
    /// any more; that answer is kept too, so a mod that went away is not asked about again every time.
    /// </summary>
    public async Task<ModIoMod?> Get(uint gameId, uint modId, bool force, CancellationToken cancellationToken)
    {
        if (!force && Fresh(gameId, modId, out var kept)) return kept;

        var mod = await api.Mod(gameId, modId, cancellationToken);
        lock (_gate) _kept[(gameId, modId)] = (time.GetUtcNow(), mod);
        return mod;
    }

    /// <summary>What was last learned about the mod, or null when nothing was or the mod is gone.</summary>
    public ModIoMod? Peek(uint gameId, uint modId)
    {
        lock (_gate) return _kept.TryGetValue((gameId, modId), out var entry) ? entry.Mod : null;
    }

    private bool Fresh(uint gameId, uint modId, out ModIoMod? mod)
    {
        lock (_gate)
        {
            if (_kept.TryGetValue((gameId, modId), out var entry) && time.GetUtcNow() - entry.At < MaxAge)
            {
                mod = entry.Mod;
                return true;
            }
        }

        mod = null;
        return false;
    }
}
