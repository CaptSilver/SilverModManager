using System.Text.RegularExpressions;
using FluentAssertions;

namespace SilverModManager.Sdk.Tests;

/// <summary>
/// Nullable warnings fail the build in <c>src</c>, so the other way to let a null through is to switch the
/// analysis off for a stretch of code: <c>#nullable disable</c>, or a pragma that silences a nullable
/// diagnostic. Neither may appear under <c>src</c>.
/// </summary>
public static partial class NullableDirectiveGuardTests
{
    // Nullable diagnostics run past CS86xx into CS87xx (CS8714, CS8762 to CS8777), hence the character class.
    [GeneratedRegex(@"^\s*#\s*nullable\s+disable\b")]
    private static partial Regex NullableDisable();

    [GeneratedRegex(@"^\s*#\s*pragma\s+warning\s+disable\b(?<codes>.*)$")]
    private static partial Regex PragmaDisable();

    [GeneratedRegex(@"\b(CS)?8[67]\d\d\b|nullable", RegexOptions.IgnoreCase)]
    private static partial Regex NullableCode();

    /// <summary>
    /// Allowlist of (file, exact trimmed line, reason) for a suppression of a CS87xx code that is not about
    /// nullability. Empty: nothing needs one today.
    /// </summary>
    private static readonly (string File, string Line, string Reason)[] Allowed = [];

    /// <summary>
    /// 1-based numbers of the lines in <paramref name="source"/> that turn nullable analysis off.
    /// </summary>
    internal static int[] FindDisabledNullableDirectives(string source)
    {
        var hits = new List<int>();
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsDisabledNullableDirective(lines[i])) hits.Add(i + 1);
        }
        return [.. hits];
    }

    private static bool IsDisabledNullableDirective(string line)
    {
        if (NullableDisable().IsMatch(line)) return true;
        var pragma = PragmaDisable().Match(line);
        if (!pragma.Success) return false;
        // A bare disable silences every warning, nullable ones included.
        var codes = pragma.Groups["codes"].Value;
        var comment = codes.IndexOf("//", StringComparison.Ordinal);
        if (comment >= 0) codes = codes[..comment];
        return string.IsNullOrWhiteSpace(codes) || NullableCode().IsMatch(codes);
    }

    [Fact]
    public static void Test_FindDisabledNullableDirectives_MatchesBothDirectiveForms()
    {
        const string source = "#nullable disable\nclass C { }\n#pragma warning disable CS8618, 8625\n#pragma warning disable CS0168\n#nullable disable warnings\n#pragma warning disable CS8714\n";
        FindDisabledNullableDirectives(source).Should().Equal(1, 3, 5, 6);
    }

    [Fact]
    public static void Test_FindDisabledNullableDirectives_MatchesBarePragmaDisable()
    {
        const string source = "#pragma warning disable\n#pragma warning disable // everything\n  #  pragma warning disable   \n#pragma warning disable CS1591 // docs\n";
        FindDisabledNullableDirectives(source).Should().Equal(1, 2, 3);
    }

    [Fact]
    public static void Test_FindDisabledNullableDirectives_IgnoresEnablingAndUnrelatedDirectives()
    {
        const string source = "#nullable enable\n#pragma warning restore CS8618\n#pragma warning disable CS1591\n#pragma warning disable nullable\nvar s = \"#nullable disable\";\n";
        FindDisabledNullableDirectives(source).Should().Equal(4);
    }

    [Fact]
    public static void Test_NoNullableDirectiveIsDisabledInSource()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var files = RepoScanGuard.EnumerateFiles(root, "src", "*.cs");
        var found = RepoScanGuard.FindLines(root, files, IsDisabledNullableDirective);

        RepoScanGuard.AssertMatchesAllowlist(found, Allowed, "nullable analysis stays on in src; fix the null instead of silencing the warning, or allowlist a non-nullable CS87xx suppression with a reason");
    }
}
