using FluentAssertions;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Sdk;
using static SilverModManager.Sdk.WineParser;

namespace SilverModManager.Platform.Proton.Tests.Steam;

public class BuiltInDxgiOverrideEmitterTests
{
    [Theory]
    [InlineData("WINEDLLOVERRIDES=\"dxgi=b\" %command%")]
    [InlineData("WINEDLLOVERRIDES=dxgi=builtin %command%")]
    [InlineData("WINEDLLOVERRIDES=\"dxgi.dll=b\" %command%")]
    [InlineData("PROTON_LOG=1 WINEDLLOVERRIDES=\"winmm=n,b;dxgi=b\" %command%")]
    [InlineData("WINEDLLOVERRIDES=\"dxgi=b,n\" %command%")]
    [InlineData("WINEDLLOVERRIDES=\"dxgi=b\" '/home/u/.local/share/silvermod/bin/silvermod' run 'Main' -- %command%")]
    public void ForcingTheBuiltInDxgiIsFlagged(string line)
    {
        BuiltInDxgiOverrideEmitter.ForcesBuiltInDxgi(line).Should().BeTrue();
    }

    [Theory]
    [InlineData("WINEDLLOVERRIDES=\"dxgi=n,b\" %command%")]
    [InlineData("WINEDLLOVERRIDES=\"dxgi=native,builtin\" %command%")]
    [InlineData("WINEDLLOVERRIDES=\"winmm=b\" %command%")]
    [InlineData("WINEDLLOVERRIDES=\"dxgi=b;dxgi=n,b\" %command%")]
    [InlineData("PROTON_USE_WINED3D=1 WINEDLLOVERRIDES=\"dxgi=b\" %command%")]
    [InlineData("gamemoderun WINEDLLOVERRIDES=\"dxgi=b\" %command%")]
    public void ADxgiThatFallsBackToDxvkOrIsNotForcedIsNotFlagged(string line)
    {
        BuiltInDxgiOverrideEmitter.ForcesBuiltInDxgi(line).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("%command%")]
    [InlineData("PROTON_LOG=1 %command% -windowed")]
    public void ALineWithoutOverridesIsNotFlagged(string line)
    {
        BuiltInDxgiOverrideEmitter.ForcesBuiltInDxgi(line).Should().BeFalse();
    }

    [Fact]
    public void WineSpellsOverrideTypesOutAsWellAsAsLetters()
    {
        ParseEnvironmentVariable("dxgi=builtin,native").Should().ContainSingle()
            .Which.OverrideTypes.Should().Equal(WineDllOverrideType.BuiltIn, WineDllOverrideType.Native);
    }
}
