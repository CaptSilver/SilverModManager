using System.Collections.Immutable;
using FluentAssertions;

namespace NexusMods.Sdk.Tests;

public class WineParserMergeTests
{
    private static readonly ImmutableArray<WineDllOverrideType> NativeThenBuiltIn = [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn];

    private static readonly IReadOnlyList<WineDllOverride> Required =
    [
        new("winmm", NativeThenBuiltIn),
        new("version", NativeThenBuiltIn),
    ];

    private static string Merge(string existing) => WineParser.MergeDllOverrides(existing, Required);

    [Fact]
    public void MergeDllOverrides_AddsEveryRequiredEntryToAnEmptyValue()
    {
        Merge("").Should().Be("winmm=n,b;version=n,b");
    }

    [Fact]
    public void MergeDllOverrides_LeavesAValueThatAlreadyHasEveryRequiredEntryUnchanged()
    {
        Merge("winmm=n,b;version=n,b").Should().Be("winmm=n,b;version=n,b");
    }

    [Fact]
    public void MergeDllOverrides_KeepsTheUsersOtherEntriesInFront()
    {
        Merge("dxgi=n,b").Should().Be("dxgi=n,b;winmm=n,b;version=n,b");
    }

    [Fact]
    public void MergeDllOverrides_ReplacesTheTypesOfARequiredDllInPlace()
    {
        Merge("dxgi=n,b;winmm=b;mscoree=").Should().Be("dxgi=n,b;winmm=n,b;mscoree=;version=n,b");
    }

    [Fact]
    public void MergeDllOverrides_MatchesDllNamesCaseInsensitivelyAndWithoutTheDllSuffix()
    {
        Merge("WinMM.dll=b").Should().Be("winmm=n,b;version=n,b");
    }

    [Fact]
    public void MergeDllOverrides_SplitsAMultiNameSectionAroundARequiredDll()
    {
        Merge("d3d11,winmm=b").Should().Be("d3d11=b;winmm=n,b;version=n,b");
    }

    [Theory]
    [InlineData("")]
    [InlineData("dxgi=n,b")]
    [InlineData("dxgi=n,b;winmm=b;mscoree=")]
    [InlineData("WinMM.dll=b")]
    [InlineData("d3d11,winmm=b")]
    [InlineData("dxgi=q")]
    [InlineData("winmm,version=b")]
    [InlineData("winmm=b;winmm=n")]
    public void MergeDllOverrides_IsIdempotent(string existing)
    {
        var once = Merge(existing);
        Merge(once).Should().Be(once);
    }

    [Fact]
    public void MergeDllOverrides_WithNoRequiredEntriesReturnsTheValueUnchanged()
    {
        WineParser.MergeDllOverrides("dxgi=n,b;mscoree=", []).Should().Be("dxgi=n,b;mscoree=");
    }

    [Fact]
    public void MergeDllOverrides_KeepsASectionWithUnknownTypesByteForByte()
    {
        // ToString throws on the empty type list the parser produces for these, so user entries must never go through it.
        Merge("dxgi=q").Should().Be("dxgi=q;winmm=n,b;version=n,b");
        Merge("d3d11,dxgi=q").Should().Be("d3d11,dxgi=q;winmm=n,b;version=n,b");
        Merge("winmm,dxgi=q").Should().Be("dxgi=q;winmm=n,b;version=n,b");
    }

    [Fact]
    public void MergeDllOverrides_DropsSectionsWithoutAnEqualsSignLikeTheParserDoes()
    {
        Merge("junk;dxgi=n").Should().Be("dxgi=n;winmm=n,b;version=n,b");
    }

    [Fact]
    public void MergeDllOverrides_NeverLeavesADuplicateDllName()
    {
        Merge("winmm=b;winmm=n;version,winmm=b").Should().Be("winmm=n,b;version=n,b");
    }

    [Fact]
    public void MergeDllOverrides_WritesADisabledRequirementAsAnEmptyTypeList()
    {
        var required = new[] { new WineDllOverride("winmm", [WineDllOverrideType.Disabled]) };
        WineParser.MergeDllOverrides("winmm=n,b", required).Should().Be("winmm=");
    }

    [Theory]
    [InlineData("")]
    [InlineData("dxgi=n,b;winmm=b;mscoree=")]
    [InlineData("d3d11,winmm=b")]
    [InlineData("dxgi=q")]
    public void MergeDllOverrides_TheMergedValueParsesBackToTheRequiredEntries(string existing)
    {
        var parsed = WineParser.ParseEnvironmentVariable(Merge(existing));

        foreach (var required in Required)
        {
            var match = parsed.Should().ContainSingle(x => x.DllName == required.DllName).Subject;
            match.OverrideTypes.Should().Equal(required.OverrideTypes);
        }
    }
}
