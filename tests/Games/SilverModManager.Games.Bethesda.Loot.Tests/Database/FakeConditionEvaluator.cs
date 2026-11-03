using SilverModManager.Games.Bethesda.Loot.Conditions;
using SilverModManager.Games.Bethesda.Loot.Database;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Database;

/// <summary>Answers conditions from a table: true or false as listed, <see cref="ConditionEvaluationException"/> for the ones in <see cref="Failing"/>.</summary>
internal sealed class FakeConditionEvaluator(IDictionary<string, bool>? answers = null) : IConditionEvaluator
{
    public Dictionary<string, bool> Answers { get; } = answers is null ? [] : new(answers);

    public HashSet<string> Failing { get; } = [];

    public List<string> Asked { get; } = [];

    public bool Evaluate(ConditionExpression expression) => Evaluate(Render(expression));

    public bool Evaluate(string condition)
    {
        Asked.Add(condition);
        if (Failing.Contains(condition)) throw new ConditionEvaluationException(condition, new IOException("unreadable"));

        return Answers.TryGetValue(condition, out var answer)
            ? answer
            : throw new InvalidOperationException($"The test gave no answer for \"{condition}\"");
    }

    // Writes back only the shapes the database tests build, so an answer can stay keyed by the condition text.
    private static string Render(ConditionExpression expression) =>
        string.Join(" or ", expression.Alternatives.Select(c => string.Join(" and ", c.Terms.Select(Render))));

    private static string Render(ConditionTerm term) => term switch
    {
        ConditionTerm.OfFunction f => (f.Negated ? "not " : "") + Render(f.Function),
        ConditionTerm.OfGroup g => (g.Negated ? "not " : "") + "(" + Render(g.Expression) + ")",
        _ => throw new NotSupportedException(term.ToString()),
    };

    private static string Render(ConditionFunction function) => function switch
    {
        ConditionFunction.Checksum f => $"checksum(\"{f.Path}\", {f.Crc:X8})",
        ConditionFunction.FilePath f => $"file(\"{f.Path}\")",
        _ => throw new NotSupportedException(function.ToString()),
    };
}
