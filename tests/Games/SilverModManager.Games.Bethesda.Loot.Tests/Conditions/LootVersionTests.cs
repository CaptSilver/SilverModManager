using SilverModManager.Games.Bethesda.Loot.Conditions;
using SilverModManager.Games.Bethesda.Loot.Database;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Conditions;

// Ports the version module's tests in loot-condition-interpreter's function/version.rs.
public class LootVersionTests
{
    private static LootVersion V(string text) => LootVersion.Parse(text);

    private static void AssertEqual(string left, string right)
    {
        V(left).Equals(V(right)).Should().BeTrue($"{left} == {right}");
        V(right).Equals(V(left)).Should().BeTrue($"{right} == {left}");
    }

    private static void AssertNotEqual(string left, string right)
    {
        V(left).Equals(V(right)).Should().BeFalse($"{left} != {right}");
        V(right).Equals(V(left)).Should().BeFalse($"{right} != {left}");
    }

    private static void AssertLess(string smaller, string larger)
    {
        V(smaller).CompareTo(V(larger)).Should().BeNegative($"{smaller} < {larger}");
        V(larger).CompareTo(V(smaller)).Should().BePositive($"{larger} > {smaller}");
    }

    private static void AssertSameOrder(string left, string right)
    {
        V(left).CompareTo(V(right)).Should().Be(0, $"{left} ~ {right}");
        V(right).CompareTo(V(left)).Should().Be(0, $"{right} ~ {left}");
    }

    [Fact]
    public void AnEmptyStringIsVersionZero()
    {
        AssertEqual("", "");
        AssertEqual("", "0");
        AssertNotEqual("", "5");
        AssertLess("", "1");
    }

    [Theory]
    [InlineData("4", "5")]
    [InlineData("5", "10")]
    [InlineData("0.0.5", "0.0.10")]
    [InlineData("0.5.0", "0.10.0")]
    [InlineData("0.5.10", "0.10.5")]
    [InlineData("5.0.0", "10.0.0")]
    [InlineData("5.10.0", "10.5.0")]
    [InlineData("5.0.10", "10.0.5")]
    [InlineData("1.0.0.0.0.0", "1.0.0.0.0.1")]
    [InlineData("1.0.0.0.0.0", "1.0.0.1")]
    [InlineData("1.0.0.0", "1.0.0.0.0.1")]
    public void NumericReleaseIdsCompareAsNumbersMostSignificantFirst(string smaller, string larger)
    {
        AssertLess(smaller, larger);
        AssertNotEqual(smaller, larger);
    }

    [Theory]
    [InlineData("5", "5")]
    [InlineData("0.0.5", "0.0.5")]
    [InlineData("05.0.0", "5.0.0")]
    [InlineData("0.05.0", "0.5.0")]
    [InlineData("0.0.05", "0.0.5")]
    [InlineData("0.0.5-05", "0.0.5-5")]
    [InlineData("1.0.0A", "1.0.0a")]
    [InlineData("1.0.0-Alpha", "1.0.0-alpha")]
    [InlineData("1-beta", "1.0.0-beta")]
    [InlineData("0.0.0.1", "0.0.0.1.0.0")]
    [InlineData("1.0.0.0.0.0", "1.0.0.0")]
    [InlineData("0.0.1+alpha", "0.0.1+beta")]
    [InlineData("0.0.1+alpha", "0.0.1+1")]
    [InlineData("0.0.1+2", "0.0.1+1")]
    public void EqualVersionsAreEqualAndOrderTheSame(string left, string right)
    {
        AssertEqual(left, right);
        AssertSameOrder(left, right);
    }

    [Fact]
    public void TheAbsenceOfPreReleaseIdsIsGreaterThanTheirPresence()
    {
        AssertNotEqual("1.0.0", "1.0.0-alpha");
        AssertLess("1.0.0-alpha", "1.0.0");
    }

    [Theory]
    [InlineData("0.0.5-5", "0.0.5-10")]
    [InlineData("0.0.5-a", "0.0.5-b")]
    [InlineData("0.0.5-9", "0.0.5-a")]
    [InlineData("0.0.5-86", "0.0.5-78b")]
    [InlineData("0.0.5-5.10", "0.0.5-10.5")]
    [InlineData("0.0.5-5", "0.0.5-5.0")]
    [InlineData("0.0.5-10", "0.0.10-5")]
    [InlineData("1.0.0-alpha", "1.0.0-Beta")]
    public void PreReleaseIdsCompareNumericBeforeTextAndEarlierIdsFirst(string smaller, string larger)
    {
        AssertLess(smaller, larger);
    }

    [Fact]
    public void PreReleaseIdsAreComparedForEquality()
    {
        AssertEqual("0.0.5-5.alpha", "0.0.5-5.alpha");
        AssertNotEqual("0.0.5-5.alpha", "0.0.5-10.beta");
    }

    [Theory]
    [InlineData("1.0.0a", "1.0.0b")]
    [InlineData("0.78b", "0.86")]
    [InlineData("1.0.0", "1.0.0a")]
    [InlineData("1.0.0alpha.2", "1.0.0beta.2")]
    [InlineData("1.0.0a", "1.0.0B")]
    public void NonNumericReleaseIdsCompareByLeadingNumberThenTextAndAreGreaterThanTheirNumber(string smaller, string larger)
    {
        AssertLess(smaller, larger);
    }

    [Fact]
    public void AReleaseIdWithNoLeadingDigitsIsGreaterThanANumber()
    {
        AssertLess("123", "one23");
    }

    [Theory]
    [InlineData("0, 2, 0, 12", "0.2.0.12")]
    [InlineData("1.0.0 alpha", "1.0.0-alpha")]
    [InlineData("1.0.0:alpha", "1.0.0-alpha")]
    [InlineData("1.0.0_alpha", "1.0.0-alpha")]
    [InlineData("1.0.0-alpha 1", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha:1", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha_1", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha-1", "1.0.0-alpha.1")]
    public void SeparatorsReadTheSameAsTheirCanonicalForm(string text, string canonical)
    {
        AssertEqual(text, canonical);
    }

    [Fact]
    public void ACommaSeparatedPlatformVersionIsAllReleaseIds()
    {
        // "1, 6, 1170, 0" holds spaces, which would otherwise start the pre-release part.
        AssertEqual("1, 6, 1170, 0", "1.6.1170.0");
        AssertLess("1, 6, 1170, 0", "1.6.1171");
    }

    [Theory]
    [InlineData(ComparisonOperator.Equal, "1.0", "1.0.0", true)]
    [InlineData(ComparisonOperator.Equal, "1.0", "1.1", false)]
    [InlineData(ComparisonOperator.NotEqual, "1.0", "1.1", true)]
    [InlineData(ComparisonOperator.NotEqual, "1.0", "1.0.0", false)]
    [InlineData(ComparisonOperator.LessThan, "1.0", "1.1", true)]
    [InlineData(ComparisonOperator.LessThan, "1.0", "1.0.0", false)]
    [InlineData(ComparisonOperator.GreaterThan, "1.2", "1.1", true)]
    [InlineData(ComparisonOperator.GreaterThan, "1.1", "1.1", false)]
    [InlineData(ComparisonOperator.LessThanOrEqual, "1.1", "1.1", true)]
    [InlineData(ComparisonOperator.LessThanOrEqual, "1.2", "1.1", false)]
    [InlineData(ComparisonOperator.GreaterThanOrEqual, "1.1", "1.1", true)]
    [InlineData(ComparisonOperator.GreaterThanOrEqual, "1.0", "1.1", false)]
    public void CompareAppliesTheOperatorToTheActualVersionOnTheLeft(ComparisonOperator op, string actual, string given, bool expected)
    {
        V(actual).Compare(op, V(given)).Should().Be(expected);
    }
}
