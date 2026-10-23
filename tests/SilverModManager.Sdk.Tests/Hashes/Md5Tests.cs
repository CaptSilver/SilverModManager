using System.Text;
using FluentAssertions;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Sdk.Tests.Hashes;

public class Md5Tests : HasherTestBase<Md5Value, Md5Hasher, Md5Tests>, IHasherTestData<Md5Value>
{
    [Theory]
    [InlineData("40C47F6AE27600001400000000000000")]
    public void Test_Hex(string input)
    {
        var expectedBytes = Convert.FromHexString(input);

        var value = Md5Value.FromHex(input);
        var actualBytes = value.AsSpan().ToArray();

        actualBytes.SequenceEqual(expectedBytes).Should().BeTrue();
    }

    [Theory]
    [InlineData("foo bar baz", "ab07acbb1e496801937adfa772424bf7")]
    public async Task Test_StreamingHasher(string input, string expectedHex)
    {
        var expected = Md5Value.FromHex(expectedHex);

        var inputBytes = Encoding.ASCII.GetBytes(input);
        var stream = new MemoryStream(inputBytes);

        var actual = await Md5Hasher.HashAsync(stream, cancellationToken: TestContext.Current.CancellationToken);
        actual.Should().Be(expected);
    }

    public static IEnumerable<(string input, Md5Value expected)> GetTestData()
    {
        yield return ("foo bar baz", Md5Value.FromHex("ab07acbb1e496801937adfa772424bf7"));
    }
}
