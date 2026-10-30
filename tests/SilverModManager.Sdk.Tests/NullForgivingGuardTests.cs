using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SilverModManager.Sdk.Tests;

/// <summary>
/// Nullable warnings are errors in <c>src</c>, so the null-forgiving operator (<c>x!</c>) is the one way a
/// null still reaches a crash. This test keeps it out of <c>src</c>; the directive and build-settings
/// guards cover the other two ways to switch the checks off.
/// </summary>
public class NullForgivingGuardTests
{
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
    public void Test_NoNullForgivingOperatorsInSource()
    {
        var offenders = Scan(RepoScanGuard.FindRepoRoot()).Select(kv => $"{kv.Key}: lines {string.Join(", ", kv.Value)}");

        string.Join(Environment.NewLine, offenders).Should().BeEmpty(
            "a null-forgiving operator hides a null that crashes at runtime; handle it with an early return, a UserFacingException, or a throw naming what was missing");
    }
}
