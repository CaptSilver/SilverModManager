using Microsoft.Extensions.Logging;

namespace SilverModManager.DataModel.Synchronizer;

/// <summary>
/// Decides how loudly a failed status evaluation is logged. Every revision of a broken loadout fails the same
/// way, so only the first failure of a run is an error; <see cref="Recovered"/> ends the run, so a later,
/// possibly different failure is an error again.
/// </summary>
internal sealed class StatusFailureLog
{
    private int _logged;

    /// <summary>
    /// The level to log this failure at.
    /// </summary>
    public LogLevel Failed() => Interlocked.Exchange(ref _logged, 1) == 0 ? LogLevel.Error : LogLevel.Debug;

    /// <summary>
    /// An evaluation succeeded: the next failure starts a new run.
    /// </summary>
    public void Recovered() => Volatile.Write(ref _logged, 0);
}
