using System.Text;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SilverModManager.Sdk.Tests;

/// <summary>
/// Nullable warnings already fail the build in <c>src</c>, so the way a null still reaches a crash is
/// the null-forgiving operator (<c>x!</c>). This ratchet counts those per file against
/// <c>NullForgivingBaseline.txt</c>: a file's count may go down but never up, and a new file starts at zero.
/// <para>
/// After removing suppressions, rewrite the baseline with
/// <c>UPDATE_NULL_FORGIVING_BASELINE=1 scripts/test.sh --project tests/SilverModManager.Sdk.Tests --filter-class SilverModManager.Sdk.Tests.NullForgivingGuardTests</c>
/// and commit the diff. The update refuses to raise a count.
/// </para>
/// </summary>
public class NullForgivingGuardTests
{
    private const string UpdateVariable = "UPDATE_NULL_FORGIVING_BASELINE";

    private static string BaselinePath(string root) => Path.Combine(root, "tests", "SilverModManager.Sdk.Tests", "NullForgivingBaseline.txt");

    /// <summary>
    /// Line numbers of every null-forgiving operator in <paramref name="source"/>. Code in an inactive
    /// <c>#if</c> branch has no syntax nodes, so the file is parsed with none of its conditional symbols and
    /// again with all of them, and the hits are merged. A branch that needs only some symbols defined (an
    /// <c>#elif</c> after a true <c>#if</c>, say) is still missed.
    /// </summary>
    internal static int[] FindSuppressionLines(string source)
    {
        var plain = CSharpSyntaxTree.ParseText(source);
        var symbols = plain.GetRoot()
            .DescendantNodes(descendIntoTrivia: true)
            .OfType<ConditionalDirectiveTriviaSyntax>()
            .SelectMany(directive => directive.Condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            .Select(name => name.Identifier.ValueText)
            .Distinct()
            .ToArray();

        var trees = symbols.Length == 0
            ? [plain]
            : new[] { plain, CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(preprocessorSymbols: symbols)) };

        return trees
            .SelectMany(tree => tree.GetRoot()
                .DescendantNodes()
                .OfType<PostfixUnaryExpressionSyntax>()
                .Where(node => node.IsKind(SyntaxKind.SuppressNullableWarningExpression))
                .Select(node => (tree, node.OperatorToken.SpanStart)))
            .DistinctBy(hit => hit.SpanStart)
            .Select(hit => hit.tree.GetLineSpan(new TextSpan(hit.SpanStart, 1)).StartLinePosition.Line + 1)
            .Order()
            .ToArray();
    }

    private static Dictionary<string, int[]> Scan(string root)
    {
        var result = new Dictionary<string, int[]>();
        foreach (var file in RepoScanGuard.EnumerateFiles(root, "src", "*.cs").OrderBy(f => f, StringComparer.Ordinal))
        {
            var lines = FindSuppressionLines(File.ReadAllText(Path.Combine(root, file)));
            if (lines.Length > 0) result[file] = lines;
        }
        return result;
    }

    /// <summary>
    /// Files whose count rose above the baseline (a file missing from it is allowed none), and baseline
    /// entries whose file now has fewer, which must be written back so the gain can't be spent later.
    /// </summary>
    internal static (string[] Raised, string[] Stale) Compare(IReadOnlyDictionary<string, int[]> current, IReadOnlyDictionary<string, int> baseline)
    {
        var raised = current
            .Where(kv => kv.Value.Length > baseline.GetValueOrDefault(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}: {kv.Value.Length} null-forgiving operators, baseline {baseline.GetValueOrDefault(kv.Key)}; at lines {string.Join(", ", kv.Value)}")
            .ToArray();

        var stale = baseline
            .Select(kv => (File: kv.Key, Allowed: kv.Value, Now: current.GetValueOrDefault(kv.Key)?.Length ?? 0))
            .Where(entry => entry.Now < entry.Allowed)
            .OrderBy(entry => entry.File, StringComparer.Ordinal)
            .Select(entry => $"{entry.File}: baseline {entry.Allowed}, now {entry.Now}")
            .ToArray();

        return (raised, stale);
    }

    private static Dictionary<string, int> ReadBaseline(string path)
    {
        var baseline = new Dictionary<string, int>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var tab = line.LastIndexOf('\t');
            baseline[line[..tab]] = int.Parse(line[(tab + 1)..]);
        }
        return baseline;
    }

    [Fact]
    public void Test_FindSuppressionLines_CountsOnlyNullForgivingOperators()
    {
        const string source = """
            class C
            {
                string? s;
                bool M(string? a, bool b)
                {
                    var x = a!;
                    var y = s!.Length + (a!).Length;
                    return !b && a != null;
                }
            }
            """;

        FindSuppressionLines(source).Should().Equal(6, 7, 7);
    }

    [Fact]
    public void Test_FindSuppressionLines_CountsBothSidesOfConditionalCompilation()
    {
        const string source = """
            class C
            {
                void M(string? a)
                {
            #if DEBUG
                    var x = a!;
            #else
                    var y = a!;
            #endif
            #if !LINUX
                    var z = a!;
            #endif
                }
            }
            """;

        FindSuppressionLines(source).Should().Equal(6, 8, 11);
    }

    [Fact]
    public void Test_Compare_FailsAFileWhoseCountRoseNamingEachLine()
    {
        var current = new Dictionary<string, int[]> { ["src/A.cs"] = [3, 9], ["src/New.cs"] = [5] };
        var baseline = new Dictionary<string, int> { ["src/A.cs"] = 1 };

        var (raised, stale) = Compare(current, baseline);

        raised.Should().Equal(
            "src/A.cs: 2 null-forgiving operators, baseline 1; at lines 3, 9",
            "src/New.cs: 1 null-forgiving operators, baseline 0; at lines 5");
        stale.Should().BeEmpty();
    }

    [Fact]
    public void Test_Compare_ReportsLoweredCountsAsStaleBaseline()
    {
        var current = new Dictionary<string, int[]> { ["src/A.cs"] = [3], ["src/Same.cs"] = [1, 2] };
        var baseline = new Dictionary<string, int> { ["src/A.cs"] = 2, ["src/Same.cs"] = 2, ["src/Gone.cs"] = 4 };

        var (raised, stale) = Compare(current, baseline);

        raised.Should().BeEmpty();
        stale.Should().Equal("src/A.cs: baseline 2, now 1", "src/Gone.cs: baseline 4, now 0");
    }

    [Fact]
    public void Test_NoNewNullForgivingOperatorsInSource()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var current = Scan(root);
        var baselinePath = BaselinePath(root);

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            var existing = File.Exists(baselinePath) ? ReadBaseline(baselinePath) : null;
            if (existing is not null)
            {
                Compare(current, existing).Raised.Should().BeEmpty($"{UpdateVariable} only lowers counts; remove the new '!' instead of baselining it");
            }

            var sb = new StringBuilder();
            sb.Append("# Null-forgiving operators (x!) per file under src. Counts may only go down.\n");
            sb.Append("# Regenerate with UPDATE_NULL_FORGIVING_BASELINE=1, see NullForgivingGuardTests.\n");
            foreach (var (file, lines) in current) sb.Append(file).Append('\t').Append(lines.Length).Append('\n');
            File.WriteAllText(baselinePath, sb.ToString());
            return;
        }

        File.Exists(baselinePath).Should().BeTrue($"{baselinePath} is the checked-in per-file baseline");
        var baseline = ReadBaseline(baselinePath);

        var (raised, stale) = Compare(current, baseline);

        string.Join(Environment.NewLine, raised).Should().BeEmpty(
            "new '!' suppressions hide nulls that crash at runtime; handle the null (early return, diagnostic, or a throw naming what was missing)");
        string.Join(Environment.NewLine, stale).Should().BeEmpty(
            $"counts went down, lock the gain in by regenerating the baseline with {UpdateVariable}=1");
    }
}
