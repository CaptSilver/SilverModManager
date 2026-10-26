using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.TestFramework;

/// <summary>
/// A prefix inspector that answers from a function instead of reading a prefix, and records what it was
/// asked. By default every component is installed, so a test only sees prefix diagnostics when it asks for them.
/// </summary>
public class FakePrefixInspector : IPrefixInspector
{
    private readonly List<(ResolvedPrefix Prefix, IReadOnlyList<PrefixComponentId> Components)> _calls = [];

    /// <summary>
    /// The state reported for each component.
    /// </summary>
    public Func<PrefixComponentId, PrefixComponentState> State { get; set; } = _ => PrefixComponentState.Installed;

    /// <summary>
    /// When set, <see cref="Inspect"/> throws it, as a prefix the app cannot read would.
    /// </summary>
    public Exception? Failure { get; set; }

    /// <summary>
    /// Every <see cref="Inspect"/> call so far.
    /// </summary>
    public IReadOnlyList<(ResolvedPrefix Prefix, IReadOnlyList<PrefixComponentId> Components)> Calls
    {
        get { lock (_calls) return _calls.ToArray(); }
    }

    /// <inheritdoc />
    public IReadOnlyList<PrefixComponentStatus> Inspect(ResolvedPrefix prefix, IReadOnlyList<PrefixComponentId> components)
    {
        lock (_calls) _calls.Add((prefix, components));
        if (Failure is not null) throw Failure;
        return components.Select(id => new PrefixComponentStatus(id, State(id), $"fake: {State(id)}")).ToList();
    }
}
