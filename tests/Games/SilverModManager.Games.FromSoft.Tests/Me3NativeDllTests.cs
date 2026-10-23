using FluentAssertions;
using SilverModManager.Games.FromSoft.Me3;

namespace SilverModManager.Games.FromSoft.Tests;

public class Me3NativeDllTests
{
    [Fact]
    public void ADllNamedLikeTheModIsPreferredOverAnotherOne()
    {
        Me3NativeDll.Find(["redist/vcruntime.dll", "Cool Mod.dll"], "Cool-Mod").Should().Be("Cool Mod.dll");
    }

    [Fact]
    public void TheNameMatchIgnoresCaseAndAnythingThatIsNotALetterOrDigit()
    {
        Me3NativeDll.Find(["a.dll", "COOL_mod_v2.dll"], "cool mod v2").Should().Be("COOL_mod_v2.dll");
    }

    [Fact]
    public void ALoneDllIsTheChoiceWhateverItIsCalled()
    {
        Me3NativeDll.Find(["bin/other.dll", "readme.txt"], "Cool Mod").Should().Be("bin/other.dll");
    }

    [Fact]
    public void SeveralDllsWithoutANameMatchHaveNoChoice()
    {
        Me3NativeDll.Find(["a.dll", "b.dll"], "Cool Mod").Should().BeNull();
    }

    [Fact]
    public void TwoDllsThatBothMatchTheNameHaveNoChoice()
    {
        Me3NativeDll.Find(["x/mod.dll", "y/mod.dll"], "mod").Should().BeNull();
    }

    [Fact]
    public void NoDllMeansNoChoice()
    {
        Me3NativeDll.Find(["readme.txt"], "mod").Should().BeNull();
    }

    [Fact]
    public void TheDllExtensionIsMatchedWithoutRegardToCase()
    {
        Me3NativeDll.Find(["Mod.DLL"], "mod").Should().Be("Mod.DLL");
    }

    [Fact]
    public void TheCandidatesAreListedInOrdinalOrder()
    {
        Me3NativeDll.ListDlls(["b.dll", "notes.txt", "A.dll", "a/c.dll"]).Should().Equal("A.dll", "a/c.dll", "b.dll");
    }
}
