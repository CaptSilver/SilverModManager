namespace SilverModManager.UI.Tests.Framework;

/// <summary>
/// Keeps the polls' job drains from nesting. A drain runs whatever the other tests have queued, and their polls drain
/// too. Nested, each poll sat inside the one before it, none could return while the others kept posting new polls,
/// and every test waiting on one of them timed out after two minutes. A poll that a drain runs leaves the rest of the
/// queue to that drain; it just reads the page, and its retry comes round again.
/// </summary>
internal static class JobDrain
{
    [ThreadStatic] private static bool _draining;

    /// <summary>
    /// Runs <paramref name="drain"/> unless one is already running on this thread. Only for a caller that tries again
    /// when what it reads is not there yet, since it cannot know whether its drain ran.
    /// </summary>
    public static void RunUnlessDraining(Action drain)
    {
        if (_draining) return;
        RunAlways(drain);
    }

    /// <summary>
    /// Runs <paramref name="drain"/> even inside another drain, for a caller that reads the page once and needs the
    /// queued jobs run first. The polls it runs along the way then skip their own drain.
    /// </summary>
    public static void RunAlways(Action drain)
    {
        var was = _draining;
        _draining = true;
        try
        {
            drain();
        }
        finally
        {
            _draining = was;
        }
    }
}
