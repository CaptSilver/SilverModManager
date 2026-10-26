namespace SilverModManager.Abstractions.Games;

/// <summary>
/// How a game is told to keep one loadout's saves apart from every other loadout's.
/// </summary>
/// <remarks>
/// Closed on purpose: consumers switch over the three cases, and a new case must be handled everywhere.
/// </remarks>
public abstract record SaveRedirectPlan
{
    private SaveRedirectPlan() { }

    /// <summary>
    /// The game reads the save location from a key in one of its INI files.
    /// </summary>
    public sealed record IniKey(string Section, string Key, string Value) : SaveRedirectPlan;

    /// <summary>
    /// A mod loader reads the save name or location from one of its own settings.
    /// </summary>
    public sealed record LoaderKey(string Name, string Value) : SaveRedirectPlan;

    /// <summary>
    /// The game has an implementation but cannot keep saves per loadout for this loadout. The reason is a sentence
    /// shown to the user as is.
    /// </summary>
    public sealed record Unsupported(string Reason) : SaveRedirectPlan;
}
