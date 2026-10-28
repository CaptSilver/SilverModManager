namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Keeps the answer to the backup-cap question for the rest of one operation. A loadout switch, a backend move or a
/// delete is several syncs in a row (the old loadout's, a reset, the new loadout's), and each would put the same
/// question to the player again. The first answer is the preset for the legs after it.
/// </summary>
/// <remarks>
/// Ambient on purpose: the legs are separate calls through the loadout manager and the synchronizer, and none of them
/// returns what it asked. A scope that starts inside another one joins it, so the outermost operation owns the answer.
/// </remarks>
public sealed class BackupOverflowAnswer
{
    private static readonly AsyncLocal<BackupOverflowAnswer?> Current = new();

    private BackupOverflowChoice? _answer;

    /// <summary>The scope the current operation runs in, or null outside one.</summary>
    internal static BackupOverflowAnswer? Active => Current.Value;

    /// <summary>The player's answer, once the question has been put.</summary>
    internal BackupOverflowChoice? Remembered => _answer;

    internal void Remember(BackupOverflowChoice choice) => _answer = choice;

    /// <summary>Starts a scope, or joins the one already open. Dispose it when the operation ends.</summary>
    public static IDisposable Begin()
    {
        if (Current.Value is not null) return Joined.Instance;

        var scope = new BackupOverflowAnswer();
        Current.Value = scope;
        return new Owner(scope);
    }

    private sealed class Owner(BackupOverflowAnswer scope) : IDisposable
    {
        public void Dispose()
        {
            if (ReferenceEquals(Current.Value, scope)) Current.Value = null;
        }
    }

    private sealed class Joined : IDisposable
    {
        public static readonly Joined Instance = new();
        public void Dispose() { }
    }
}
