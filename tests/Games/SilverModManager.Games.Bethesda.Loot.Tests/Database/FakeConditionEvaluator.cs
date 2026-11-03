using SilverModManager.Games.Bethesda.Loot.Conditions;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Database;

/// <summary>Answers conditions from a table: true or false as listed, <see cref="ConditionEvaluationException"/> for the ones in <see cref="Failing"/>.</summary>
internal sealed class FakeConditionEvaluator(IDictionary<string, bool>? answers = null) : IConditionEvaluator
{
    public Dictionary<string, bool> Answers { get; } = answers is null ? [] : new(answers);

    public HashSet<string> Failing { get; } = [];

    public List<string> Asked { get; } = [];

    public bool Evaluate(string condition)
    {
        Asked.Add(condition);
        if (Failing.Contains(condition)) throw new ConditionEvaluationException(condition, new IOException("unreadable"));

        return Answers.TryGetValue(condition, out var answer)
            ? answer
            : throw new InvalidOperationException($"The test gave no answer for \"{condition}\"");
    }
}
