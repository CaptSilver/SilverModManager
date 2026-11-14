using SilverModManager.Platform.Proton.ToolRunner;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tests;

/// <summary>
/// Builds the real running-game detector over a test's tracker. Nothing in the test host's own chain of parents is
/// ever listed by a fake tracker, so by default no ancestors are looked up.
/// </summary>
internal static class TestDetectors
{
    public const int OwnPid = 4000;

    public static RunningGameDetector Over(
        IProcessTracker tracker,
        OwnProcessFamily? family = null,
        IStartedTools? tools = null,
        Func<int, int?>? parentOf = null,
        int ownPid = OwnPid)
        => new(tracker, tools ?? new StartedTools(), TimeProvider.System, null, parentOf ?? (_ => null),
            family ?? new OwnProcessFamily(_ => null, _ => null, ownPid), ownPid, TimeSpan.FromMilliseconds(10));
}
