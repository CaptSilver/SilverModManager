using SilverModManager.Games.Bethesda.Loot.Database;

namespace SilverModManager.Games.Bethesda.Loot.Conditions;

/// <summary>Decides whether a LOOT condition string holds.</summary>
public interface IConditionEvaluator
{
    /// <exception cref="ConditionParseException">The text is not a condition.</exception>
    /// <exception cref="ConditionEvaluationException">A file the condition needs could not be read.</exception>
    bool Evaluate(string condition);
}
