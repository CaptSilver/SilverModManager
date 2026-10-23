using NexusMods.Games.Bethesda.Loot.Database;

namespace NexusMods.Games.Bethesda.Loot.Tests.Database;

// Ports the parse tests in loot-condition-interpreter's lib.rs and function/parse.rs.
public class ConditionExpressionTests
{
    private static ConditionFunction Single(string condition)
    {
        var functions = ConditionExpression.Parse(condition).Functions;
        functions.Should().ContainSingle();
        return functions[0];
    }

    private static Action Parsing(string condition) => () => ConditionExpression.Parse(condition);

    [Fact]
    public void ShouldParseAFilePathFunction() =>
        Single("file(\"Cargo.toml\")").Should().Be(new ConditionFunction.FilePath("Cargo.toml"));

    [Fact]
    public void ShouldParseAFileRegexFunctionWithNoParentPath() =>
        Single("file(\"Cargo.*\")").Should().Be(new ConditionFunction.FileRegex(".", "^Cargo.*$"));

    [Fact]
    public void ShouldParseAFileRegexFunctionWithAParentPath() =>
        Single("file(\"subdir/Cargo.*\")").Should().Be(new ConditionFunction.FileRegex("subdir", "^Cargo.*$"));

    [Fact]
    public void ShouldParseAFileSizeFunction() =>
        Single("file_size(\"Cargo.toml\", 1234)").Should().Be(new ConditionFunction.FileSize("Cargo.toml", 1234));

    [Fact]
    public void ShouldParseAReadableFunction() =>
        Single("readable(\"Cargo.toml\")").Should().Be(new ConditionFunction.Readable("Cargo.toml"));

    [Fact]
    public void ShouldParseAnIsExecutableFunction() =>
        Single("is_executable(\"Cargo.toml\")").Should().Be(new ConditionFunction.IsExecutable("Cargo.toml"));

    [Fact]
    public void ShouldParseAnActivePathFunction() =>
        Single("active(\"Cargo.toml\")").Should().Be(new ConditionFunction.ActivePath("Cargo.toml"));

    [Fact]
    public void ShouldParseAnActiveRegexFunction() =>
        Single("active(\"Cargo.*\")").Should().Be(new ConditionFunction.ActiveRegex("^Cargo.*$"));

    [Fact]
    public void ShouldParseAnIsMasterFunction() =>
        Single("is_master(\"Blank.esm\")").Should().Be(new ConditionFunction.IsMaster("Blank.esm"));

    [Fact]
    public void ShouldParseAManyFunctionWithNoParentPath() =>
        Single("many(\"Cargo.*\")").Should().Be(new ConditionFunction.Many(".", "^Cargo.*$"));

    [Fact]
    public void ShouldParseAManyFunctionWithAParentPath() =>
        Single("many(\"subdir/Cargo.*\")").Should().Be(new ConditionFunction.Many("subdir", "^Cargo.*$"));

    [Fact]
    public void ShouldParseAManyActiveFunction() =>
        Single("many_active(\"Cargo.*\")").Should().Be(new ConditionFunction.ManyActive("^Cargo.*$"));

    [Fact]
    public void ShouldParseAChecksumFunction() =>
        Single("checksum(\"Cargo.toml\", DEADBEEF)").Should().Be(new ConditionFunction.Checksum("Cargo.toml", 0xDEADBEEF));

    [Fact]
    public void ShouldParseAChecksumWithLowercaseDigits() =>
        Single("checksum(\"Cargo.toml\",deadbeef)").Should().Be(new ConditionFunction.Checksum("Cargo.toml", 0xDEADBEEF));

    [Theory]
    [InlineData("==", ComparisonOperator.Equal)]
    [InlineData("!=", ComparisonOperator.NotEqual)]
    [InlineData("<", ComparisonOperator.LessThan)]
    [InlineData(">", ComparisonOperator.GreaterThan)]
    [InlineData("<=", ComparisonOperator.LessThanOrEqual)]
    [InlineData(">=", ComparisonOperator.GreaterThanOrEqual)]
    public void ShouldParseEveryComparatorInAVersionFunction(string text, ComparisonOperator expected)
    {
        Single($"version(\"Cargo.toml\", \"1.2\", {text})")
            .Should().Be(new ConditionFunction.Version("Cargo.toml", "1.2", expected));
    }

    [Fact]
    public void ShouldParseAVersionPathContainingBackslashes() =>
        Single("version(\"..\\Cargo.toml\", \"1.2\", ==)")
            .Should().Be(new ConditionFunction.Version("..\\Cargo.toml", "1.2", ComparisonOperator.Equal));

    [Fact]
    public void ShouldParseAVersionWithTheComparatorAsTheSecondArgument() =>
        Single("version(\"..\\Cargo.toml\", ==, \"1.2\")")
            .Should().Be(new ConditionFunction.Version("..\\Cargo.toml", "1.2", ComparisonOperator.Equal));

    [Fact]
    public void ShouldParseAProductVersionFunction() =>
        Single("product_version(\"Cargo.toml\", \"1.2\", ==)")
            .Should().Be(new ConditionFunction.ProductVersion("Cargo.toml", "1.2", ComparisonOperator.Equal));

    [Fact]
    public void ShouldParseAProductVersionWithTheComparatorAsTheSecondArgument() =>
        Single("product_version(\"Cargo.toml\", >=, \"1.2\")")
            .Should().Be(new ConditionFunction.ProductVersion("Cargo.toml", "1.2", ComparisonOperator.GreaterThanOrEqual));

    [Fact]
    public void ShouldParseAFilenameVersionFunction() =>
        Single("filename_version(\"subdir/Cargo (.+).toml\", \"1.2\", ==)")
            .Should().Be(new ConditionFunction.FilenameVersion("subdir", "^Cargo (.+).toml$", "1.2", ComparisonOperator.Equal));

    [Fact]
    public void ShouldParseAFilenameVersionWithTheComparatorAsTheSecondArgument() =>
        Single("filename_version(\"subdir/Cargo (.+).toml\", ==, \"1.2\")")
            .Should().Be(new ConditionFunction.FilenameVersion("subdir", "^Cargo (.+).toml$", "1.2", ComparisonOperator.Equal));

    [Fact]
    public void ShouldParseADescriptionContainsFunctionWithAnUnanchoredRegex()
    {
        var lowercaseNonAscii = "€ƒ.";

        Single($"description_contains(\"Blank.esp\", \"{lowercaseNonAscii}\")")
            .Should().Be(new ConditionFunction.DescriptionContains("Blank.esp", lowercaseNonAscii));
    }

    [Fact]
    public void ShouldIgnoreWhitespaceBetweenFunctionArguments()
    {
        string[] valid =
        [
            "version(\"Cargo.toml\", \"1.2\", ==)",
            "version(\"Unofficial Oblivion Patch.esp\",\"3.4.0\",>=)",
            "version(\"..\\TESV.exe\", \"1.8\", >) and not checksum(\"EternalShineArmorAndWeapons.esp\",3E85A943)",
            "version(\"..\\TESV.exe\",\"1.8\",>) and not checksum(\"EternalShineArmorAndWeapons.esp\",3E85A943)",
            "checksum(\"HM_HotkeyMod.esp\",374C564C) or checksum(\"HM_HotkeyMod.esp\",CF00AFFD)",
            "( checksum(\"HM_HotkeyMod.esp\",374C564C) or checksum(\"HM_HotkeyMod.esp\",CF00AFFD) ) and file(\"UFO - Ultimate Follower Overhaul.esp\")",
            "many(\"Deeper Thoughts (\\(Curie\\)|- (Expressive )?Curie)\\.esp\")",
        ];

        foreach (var condition in valid) Parsing(condition).Should().NotThrow(condition);
    }

    [Fact]
    public void ShouldIgnoreLineBreaksBetweenTokens()
    {
        var expression = ConditionExpression.Parse(
            "file(\"Cargo.toml\")\r\nor\nversion(\"Cargo.toml\",\n\"1.2\",\r\n==)\nand\r\nfile(\"Cargo.toml\")");

        expression.Alternatives.Select(a => a.Terms.Count).Should().Equal(1, 2);
    }

    [Fact]
    public void ShouldBindAndTighterThanOr()
    {
        var expression = ConditionExpression.Parse("file(\"a\") or file(\"b\") and file(\"c\") or file(\"d\")");

        expression.Alternatives.Select(a => a.Terms.Count).Should().Equal(1, 2, 1);
    }

    [Fact]
    public void ShouldParseNotBeforeAFunction()
    {
        var expression = ConditionExpression.Parse("not file(\"Cargo.toml\")");

        expression.Alternatives.Single().Terms.Single()
            .Should().Be(new ConditionTerm.OfFunction(new ConditionFunction.FilePath("Cargo.toml"), Negated: true));
    }

    [Fact]
    public void ShouldParseAFunctionWithoutNotAsNotNegated()
    {
        ConditionExpression.Parse("file(\"Cargo.toml\")").Alternatives.Single().Terms.Single()
            .Should().Be(new ConditionTerm.OfFunction(new ConditionFunction.FilePath("Cargo.toml"), Negated: false));
    }

    [Theory]
    [InlineData("(not file(\"Cargo.toml\"))", false)]
    [InlineData("( not file(\"Cargo.toml\") )", false)]
    [InlineData("not(not file(\"Cargo.toml\"))", true)]
    [InlineData("not ( not file(\"Cargo.toml\") )", true)]
    public void ShouldParseAParenthesisedExpression(string condition, bool negated)
    {
        var term = ConditionExpression.Parse(condition).Alternatives.Single().Terms.Single();

        var group = term.Should().BeOfType<ConditionTerm.OfGroup>().Subject;
        group.Negated.Should().Be(negated);
        group.Expression.Functions.Should().Equal(new ConditionFunction.FilePath("Cargo.toml"));
    }

    [Fact]
    public void ShouldListFunctionsInsideGroupsInSourceOrder()
    {
        var expression = ConditionExpression.Parse(
            "file(\"a\") and (file(\"b\") or not (readable(\"c\") and file(\"d\"))) or file(\"e\")");

        expression.Functions
            .Select(f => f switch
            {
                ConditionFunction.FilePath file => file.Path,
                ConditionFunction.Readable readable => readable.Path,
                _ => throw new InvalidOperationException(f.ToString()),
            })
            .Should().Equal("a", "b", "c", "d", "e");
    }

    [Fact]
    public void ShouldParseTheEmptyStringAsAnExpressionWithNoFunctions()
    {
        ConditionExpression.Parse("").Functions.Should().BeEmpty();
    }

    [Fact]
    public void ShouldAcceptAnEmptyAlternativeAfterOr()
    {
        // The grammar's alternative list parses zero conditions as a valid alternative, so a dangling
        // "or" is not trailing input.
        var expression = ConditionExpression.Parse("file(\"a\") or");

        expression.Alternatives.Select(a => a.Terms.Count).Should().Equal(1, 0);
    }

    [Fact]
    public void ShouldRejectTrailingInput()
    {
        Parsing("file(\"Cargo.toml\") foobar").Should().Throw<ConditionParseException>()
            .WithMessage("The parser did not consume the following input: \" foobar\"");
    }

    [Theory]
    [InlineData("file(\"a\") and")]
    [InlineData("file(\"a\") and or file(\"b\")")]
    [InlineData("file(\"a\"")]
    [InlineData("(file(\"a\")")]
    [InlineData("file(\"a\"))")]
    [InlineData("nope(\"a\")")]
    [InlineData("file(\"\")")]
    public void ShouldRejectInputThatDoesNotEndWhereTheExpressionDoes(string condition)
    {
        Parsing(condition).Should().Throw<ConditionParseException>();
    }

    [Theory]
    [InlineData(" file(\"a\")")]
    [InlineData("file(\"a\") ")]
    public void ShouldRejectWhitespaceAroundTheWholeExpression(string condition)
    {
        // Whitespace is only skipped around the keywords and brackets, as in the interpreter.
        Parsing(condition).Should().Throw<ConditionParseException>();
    }

    [Theory]
    [InlineData("file(\"sub\\dir/\")")]
    [InlineData("many(\"subdir/\")")]
    [InlineData("filename_version(\"subdir/\", \"1\", ==)")]
    public void ShouldRejectARegexPathEndingInADirectorySeparator(string condition)
    {
        Parsing(condition).Should().Throw<ConditionParseException>().WithMessage("*ends in a directory separator*");
    }

    [Fact]
    public void ShouldRejectARegexThatDoesNotCompile()
    {
        Parsing("many(\"Cargo(\")").Should().Throw<ConditionParseException>();
        Parsing("description_contains(\"a.esp\", \"[\")").Should().Throw<ConditionParseException>();
    }

    [Theory]
    [InlineData("filename_version(\"subdir/Cargo .+.toml\", \"1.2\", ==)")]
    [InlineData("filename_version(\"subdir/Cargo (.+) (.+).toml\", \"1.2\", ==)")]
    public void ShouldRejectAFilenameVersionRegexWithoutExactlyOneCaptureGroup(string condition)
    {
        Parsing(condition).Should().Throw<ConditionParseException>();
    }

    [Fact]
    public void ShouldCountNonCapturingGroupsAsNotCapturing()
    {
        Single("filename_version(\"Cargo (?:x)(.+).toml\", \"1.2\", ==)").Should().BeOfType<ConditionFunction.FilenameVersion>();
    }

    [Fact]
    public void ShouldRejectANumberThatOverflows()
    {
        Parsing("checksum(\"a.esp\", 1FFFFFFFF)").Should().Throw<ConditionParseException>();
        Parsing("file_size(\"a.esp\", 18446744073709551616)").Should().Throw<ConditionParseException>();
    }

    [Theory]
    [InlineData("many(\"a(?=b)\\.esp\")", "lookahead")]
    [InlineData("many(\"a(?!b)\\.esp\")", "negative lookahead")]
    [InlineData("description_contains(\"a.esp\", \"(?<=a)b\")", "lookbehind")]
    [InlineData("description_contains(\"a.esp\", \"(?<!a)b\")", "negative lookbehind")]
    [InlineData("many_active(\"(a)\\1\\.esp\")", "backreference")]
    [InlineData("description_contains(\"a.esp\", \"(?<n>a)\\k<n>\")", "backreference")]
    [InlineData("active(\"(a)\\2\")", "backreference")]
    [InlineData("many_active(\"a\\0\\.esp\")", "backreference")]
    [InlineData("file(\"sub/(a)\\1\")", "backreference")]
    [InlineData("description_contains(\"a.esp\", \"x(?=y)\")", "lookahead")]
    [InlineData("filename_version(\"(a)(?=b) (.+)\\.esp\", \"1\", ==)", "lookahead")]
    public void ShouldRejectRegexConstructsTheRustRegexCrateRejects(string condition, string construct)
    {
        Parsing(condition).Should().Throw<ConditionParseException>().WithMessage($"*{construct}*");
    }

    [Theory]
    [InlineData("many(\"a\\(?=b\\.esp\")")]
    [InlineData("many(\"a[(?=]b\\.esp\")")]
    [InlineData("many(\"a\\\\(?:b)\\.esp\")")]
    [InlineData("description_contains(\"a.esp\", \"(?<name>a)b\")")]
    [InlineData("many(\"a(?i)b\\.esp\")")]
    public void ShouldAcceptRegexesThatOnlyLookLikeAssertionsOrBackreferences(string condition)
    {
        Parsing(condition).Should().NotThrow();
    }

    [Theory]
    [InlineData("description_contains(\"a.esp\", \"(?>a)b\")", "atomic group")]
    [InlineData("many(\"(?(a)b|c)\\.esp\")", "conditional")]
    [InlineData("many(\"a(?#comment)\\.esp\")", "comment")]
    [InlineData("many(\"\\Ga\\.esp\")", "escape")]
    [InlineData("many(\"a\\.esp\\Z\")", "escape")]
    [InlineData("many(\"a\\q\\.esp\")", "escape")]
    [InlineData("many(\"a\\e\\.esp\")", "escape")]
    [InlineData("many(\"a{\\.esp\")", "repetition")]
    [InlineData("many(\"a{x}\\.esp\")", "repetition")]
    [InlineData("many(\"a{,3}\\.esp\")", "repetition")]
    [InlineData("many(\"(?'n'a)\\.esp\")", "group")]
    [InlineData("many(\"(?P=n)\\.esp\")", "group")]
    [InlineData("description_contains(\"a.esp\", \"[\\<]\")", "escape")]
    public void ShouldRejectRegexSyntaxTheRustRegexCrateRejectsButDotNetAccepts(string condition, string construct)
    {
        Parsing(condition).Should().Throw<ConditionParseException>().WithMessage($"*{construct}*does not support*");
    }

    // Rust reads these as syntax .NET does not share (most of them valid there), so a condition using one
    // is refused rather than evaluated differently from LOOT.
    [Theory]
    [InlineData("many(\"[[]a\\.esp\")", "nested")]
    [InlineData("many(\"[a-z-[aeiou]]\\.esp\")", "nested")]
    [InlineData("many(\"[[:alpha:]]\\.esp\")", "nested")]
    [InlineData("many(\"[a&&b]\\.esp\")", "set operation")]
    [InlineData("many(\"[a--b]\\.esp\")", "set operation")]
    [InlineData("many(\"[a~~b]\\.esp\")", "set operation")]
    [InlineData("description_contains(\"a.esp\", \"\\<a\")", "word boundary")]
    [InlineData("description_contains(\"a.esp\", \"a\\>\")", "word boundary")]
    public void ShouldRejectRegexSyntaxTheRustRegexCrateReadsDifferently(string condition, string construct)
    {
        Parsing(condition).Should().Throw<ConditionParseException>().WithMessage($"*{construct}*reads differently*");
    }

    [Theory]
    [InlineData("many(\"a{2}\\.esp\")")]
    [InlineData("many(\"a{2,}\\.esp\")")]
    [InlineData("many(\"a{2,3}\\.esp\")")]
    [InlineData("many(\"a\\{b\\.esp\")")]
    [InlineData("many(\"[a{]\\.esp\")")]
    [InlineData("many(\"a\\-b\\.esp\")")]
    [InlineData("many(\"[a\\[]\\.esp\")")]
    [InlineData("many(\"[]a]\\.esp\")")]
    [InlineData("many(\"[a-z-]\\.esp\")")]
    [InlineData("many(\"(?i:a)\\.esp\")")]
    [InlineData("many(\"\\d\\w\\s\\b\\x41\\.esp\")")]
    [InlineData("many(\"\\p{L}\\.esp\")")]
    public void ShouldAcceptRegexSyntaxBothEnginesShare(string condition)
    {
        Parsing(condition).Should().NotThrow();
    }
}
