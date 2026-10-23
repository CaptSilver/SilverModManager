using NexusMods.Platform.Proton.Launch;

namespace NexusMods.App;

/// <summary>
/// Holds the launch session of a headless <c>run</c> process, so that <see cref="ProcessStartup"/> can end
/// it once the database is closed. Whatever starts the session sets <see cref="Current"/> before the game starts.
/// </summary>
public sealed class LaunchSessionSlot
{
    public LaunchSession? Current { get; set; }
}
