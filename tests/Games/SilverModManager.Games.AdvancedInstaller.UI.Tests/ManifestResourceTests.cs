using FluentAssertions;
using SilverModManager.Games.AdvancedInstaller.UI.Resources;

namespace SilverModManager.Games.AdvancedInstaller.UI.Tests;

/// <summary>
/// The generated resource class names its resx by a string literal in the designer file, which nothing
/// checks at build time. Reading a string goes through that name.
/// </summary>
public class ManifestResourceTests
{
    [Fact]
    public void Test_ResourceNameIsDerivedFromTheAssemblyName()
    {
        var assembly = typeof(Language).Assembly;
        var expected = $"{assembly.GetName().Name}.Resources.Language.resources";
        assembly.GetManifestResourceNames().Should().Contain(expected);
        assembly.GetManifestResourceStream(expected + ".missing").Should().BeNull();
    }

    [Fact]
    public void Test_LanguageStringsAreReadable()
        => Language.Footer_CANCEL.Should().NotBeNullOrEmpty();
}
