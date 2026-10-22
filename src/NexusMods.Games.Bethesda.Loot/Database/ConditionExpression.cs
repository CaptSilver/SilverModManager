namespace NexusMods.Games.Bethesda.Loot.Database;

// Follows loot-condition-interpreter's lib.rs (Expression, CompoundCondition, Condition).

/// <summary>
/// A parsed condition: compounds joined by <c>or</c>, each a run of terms joined by <c>and</c>. A
/// term is a function call or a parenthesised expression, either of which may be negated with
/// <c>not</c>. It can be inspected but not evaluated yet.
/// </summary>
public sealed class ConditionExpression
{
    internal ConditionExpression(IReadOnlyList<ConditionCompound> alternatives) => Alternatives = alternatives;

    /// <summary>The compounds joined by <c>or</c>. A compound may hold no terms.</summary>
    public IReadOnlyList<ConditionCompound> Alternatives { get; }

    /// <summary>Every function the condition calls, in source order, including those inside parentheses.</summary>
    public IReadOnlyList<ConditionFunction> Functions
    {
        get
        {
            List<ConditionFunction> functions = [];
            foreach (var term in Alternatives.SelectMany(a => a.Terms))
            {
                switch (term)
                {
                    case ConditionTerm.OfFunction f: functions.Add(f.Function); break;
                    case ConditionTerm.OfGroup g: functions.AddRange(g.Expression.Functions); break;
                }
            }

            return functions;
        }
    }

    /// <summary>
    /// Parses a condition. Whitespace is allowed around <c>and</c>, <c>or</c>, <c>not</c>, brackets
    /// and function arguments, but not before the first token or after the last. The empty string is
    /// an expression with no functions.
    /// </summary>
    /// <exception cref="ConditionParseException">The text is not a condition, has trailing input, or holds a regex
    /// that does not compile or that LOOT's regex engine rejects (lookaround, backreferences).</exception>
    public static ConditionExpression Parse(string condition) => ConditionParser.ParseWhole(condition);
}

/// <summary>Terms joined by <c>and</c>.</summary>
public sealed record ConditionCompound(IReadOnlyList<ConditionTerm> Terms);

public abstract record ConditionTerm(bool Negated)
{
    public sealed record OfFunction(ConditionFunction Function, bool Negated) : ConditionTerm(Negated);

    public sealed record OfGroup(ConditionExpression Expression, bool Negated) : ConditionTerm(Negated);
}
