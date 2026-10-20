using FluentAssertions;

namespace NexusMods.Deployment.Vfs.Tests;

public class VfsNameKeyTests
{
    // Every row is checked against string.Equals(OrdinalIgnoreCase) as well, which is how
    // RelativePath compares: the tree and the synchronizer must agree on what is one file.
    [Theory]
    [InlineData("Meshes", "MESHES", true)]
    [InlineData("Straße.esp", "STRASSE.ESP", false)]
    [InlineData("Straße.esp", "STRAßE.ESP", true)]
    [InlineData("ılık.dds", "ILIK.DDS", false)]
    [InlineData("é.dds", "é.dds", false)]
    [InlineData("ǅ.esp", "Ǆ.esp", true)]
    [InlineData("Ωmega.bsa", "ωMEGA.BSA", true)]
    public void KeysAgreeWithOrdinalIgnoreCase(string a, string b, bool expectedEqual)
    {
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase).Should().Be(expectedEqual);
        new VfsNameKey(a).Equals(new VfsNameKey(b)).Should().Be(expectedEqual);
    }

    [Theory]
    [InlineData("Meshes", "MESHES")]
    [InlineData("Ωmega.bsa", "ωMEGA.BSA")]
    public void EqualKeysHashAlike(string a, string b)
    {
        new VfsNameKey(a).GetHashCode().Should().Be(new VfsNameKey(b).GetHashCode());
    }
}
