using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Platform.Proton.Launch;

namespace SilverModManager.Platform.Proton.Tests.Launch;

/// <summary>
/// Builds the <see cref="PendingLaunchFinisher"/> a <see cref="LaunchSession"/> needs, over the same fakes the
/// session under test uses. The tracker sees no game process unless a test says otherwise.
/// </summary>
internal static class TestFinishers
{
    private const int OwnPid = 4000;

    public static PendingLaunchFinisher For(
        IInstallationLockFactory locks,
        ActiveLaunchSessions active,
        IEnumerable<ILaunchGate> gates,
        LaunchFinish finish,
        ILaunchFinishMarkers markers,
        IProcessTracker? tracker = null)
    {
        if (tracker is null)
        {
            tracker = Substitute.For<IProcessTracker>();
            tracker.FindGameProcesses(Arg.Any<GameInstallation>()).Returns(Array.Empty<TrackedProcess>());
        }
        return new PendingLaunchFinisher(Substitute.For<IConnection>(), markers, locks, tracker, active, gates, finish,
            NullLogger<PendingLaunchFinisher>.Instance, new OwnProcessFamily(_ => null, _ => null, OwnPid), _ => []);
    }
}
