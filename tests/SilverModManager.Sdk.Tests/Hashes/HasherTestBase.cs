using System.Text;
using FluentAssertions;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Sdk.Tests.Hashes;

/// <summary>
/// Supplies the (input, expected) pairs for a <see cref="HasherTestBase{THash,THasher,TSelf}"/>.
/// </summary>
/// <remarks>
/// xunit resolves [MemberData] through a static member, and an attribute on a generic base
/// class can't name the derived type. The derived test class therefore passes itself as
/// <c>TSelf</c> so the base class can reach its data through a static abstract member.
/// Inputs must be unique per class: they identify the test case, because hash values
/// such as <see cref="Md5Value"/> aren't xunit-serializable and would merge all cases into one.
/// </remarks>
public interface IHasherTestData<THash>
{
    static abstract IEnumerable<(string input, THash expected)> GetTestData();
}

public abstract class HasherTestBase<THash, THasher, TSelf>
    where THash : unmanaged, IEquatable<THash>
    where THasher : IHasher<THash, THasher>
    where TSelf : IHasherTestData<THash>
{
    public static TheoryData<string> TestInputs => new(TSelf.GetTestData().Select(x => x.input));

    [Theory]
    [MemberData(nameof(TestInputs))]
    public void Test_Hasher(string input)
    {
        var expected = TSelf.GetTestData().Single(x => x.input == input).expected;

        var bytes = Encoding.UTF8.GetBytes(input);
        var hashFromBytes = THasher.Hash(bytes);
        hashFromBytes.Should().Be(expected);
    }
}
