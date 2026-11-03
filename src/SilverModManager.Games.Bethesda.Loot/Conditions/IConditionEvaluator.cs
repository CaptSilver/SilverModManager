using SilverModManager.Games.Bethesda.Loot.Database;

namespace SilverModManager.Games.Bethesda.Loot.Conditions;

/// <summary>Decides whether a LOOT condition string holds.</summary>
public interface IConditionEvaluator
{
    /// <exception cref="ConditionParseException">The text is not a condition.</exception>
    /// <exception cref="ConditionEvaluationException">A file the condition needs could not be read.</exception>
    bool Evaluate(string condition);

    /// <summary>
    /// Decides a condition built as a tree, for the callers that assemble one from data (a plugin name, a checksum)
    /// and must not write it out as text where a quote in the data would become condition syntax.
    /// </summary>
    /// <exception cref="ConditionEvaluationException">A file the condition needs could not be read.</exception>
    bool Evaluate(ConditionExpression condition);
}
