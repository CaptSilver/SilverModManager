using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using NexusMods.Sdk.Hashes;

namespace NexusMods.Sdk.Tests.Hashes;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public class FNV1a16Tests : HasherTestBase<ushort, FNV1a16Hasher, FNV1a16Tests>, IHasherTestData<ushort>
{
    public static IEnumerable<(string input, ushort expected)> GetTestData()
    {
        yield return ("", 0x1cd9);
        yield return ("foo bar baz", 0x4049);
        yield return ("Game", 0xe151);
    }

    [Theory]
    [InlineData(0xC963, "0MBD7H9HMF", "PWXWFTVG H")]
    public void Test_HashCollision(ushort hash, string left, string right)
    {
        var pool = new StringHashPool<ushort, FNV1a16Hasher>(nameof(Test_HashCollision));
        var actual = pool.GetOrAdd(left);
        actual.Should().Be(hash);

        var act = () => pool.GetOrAdd(right);
        act.Should().ThrowExactly<HashCollisionException>();
    }
}

[SuppressMessage("ReSharper", "InconsistentNaming")]
public class FNV1a32Tests : HasherTestBase<uint, FNV1a32Hasher, FNV1a32Tests>, IHasherTestData<uint>
{
    public static IEnumerable<(string input, uint expected)> GetTestData()
    {
        yield return ("", 0x811c9dc5);
        yield return ("foo bar baz", 0xa3c6e38f);
        yield return ("Game", 0xc2762327);
    }
}

[SuppressMessage("ReSharper", "InconsistentNaming")]
public class FNV1a64Tests : HasherTestBase<ulong, FNV1a64Hasher, FNV1a64Tests>, IHasherTestData<ulong>
{
    public static IEnumerable<(string input, ulong expected)> GetTestData()
    {
        yield return ("", 0xcbf29ce484222325);
        yield return ("foo bar baz", 0x2ece4bef60afe2af);
        yield return ("Game", 0xb568a67e05a19907);
    }
}
