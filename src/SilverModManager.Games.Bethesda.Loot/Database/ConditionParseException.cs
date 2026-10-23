namespace SilverModManager.Games.Bethesda.Loot.Database;

/// <summary>
/// Thrown when a condition string is not a valid LOOT condition, including when a regex in it is
/// one LOOT's interpreter would refuse.
/// </summary>
public sealed class ConditionParseException(string message, string condition) : Exception(message)
{
    /// <summary>The whole condition that failed to parse.</summary>
    public string Condition { get; } = condition;
}
