namespace NexusMods.Games.Bethesda.LoadOrder.Ordering;

/// <summary>
/// Plugins that each need another of the group to load first. Valid plugins cannot do this, so the
/// caller refuses to write a list rather than guess.
/// </summary>
public sealed class PluginOrderCycleException : Exception
{
    public IReadOnlyList<string> Plugins { get; }

    public PluginOrderCycleException(IReadOnlyList<string> plugins)
        : base($"Plugins depend on each other in a cycle: {string.Join(", ", plugins)}")
    {
        Plugins = plugins;
    }
}
