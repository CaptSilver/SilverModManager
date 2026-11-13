using FluentAssertions;

namespace SilverModManager.Sdk.Tests;

public class WineDllOverrideTests
{
    [Theory]
    [InlineData("n,b", true)]
    [InlineData("n", true)]
    [InlineData("b,n", false)]
    [InlineData("b", false)]
    [InlineData("", false)]
    public void PrefersNative_IsTrueWhenNativeIsTriedFirst(string types, bool prefersNative)
    {
        var parsed = types.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t == "n" ? WineDllOverrideType.Native : WineDllOverrideType.BuiltIn);

        new WineDllOverride("version", [.. parsed]).PrefersNative.Should().Be(prefersNative);
    }
}
