using FluentAssertions;

namespace NexusMods.Games.Bethesda.LoadOrder.Tests;

public class PluginNameComparerTests
{
    [Fact]
    public void IgnoresAsciiCase() => PluginNameComparer.Instance.Equals("Skyrim.ESM", "skyrim.esm").Should().BeTrue();

    [Fact]
    public void HashesCaseInsensitively()
    {
        PluginNameComparer.Instance.GetHashCode("Skyrim.ESM").Should().Be(PluginNameComparer.Instance.GetHashCode("skyrim.esm"));
    }

    [Fact]
    public void IsOrdinalNotCultureAware()
    {
        // Kelvin sign U+212A lower-cases to 'k' under culture rules but not under ordinal ones.
        PluginNameComparer.Instance.Equals("Key.esp", "key.esp").Should().BeFalse();
    }

    [Fact]
    public void WorksAsADictionaryComparer()
    {
        var map = new Dictionary<string, int>(PluginNameComparer.Instance) { ["A.esp"] = 1 };

        map["a.ESP"].Should().Be(1);
    }
}
