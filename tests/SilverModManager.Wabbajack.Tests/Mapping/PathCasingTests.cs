using FluentAssertions;
using NexusMods.Paths;
using NexusMods.Wabbajack.Mapping;

namespace NexusMods.Wabbajack.Tests.Mapping;

public class PathCasingTests
{
    private static RelativePath P(string path) => RelativePath.FromUnsanitizedInput(path);

    // RelativePath equality ignores case, so a spelling is only checked by comparing the text.
    private static string Spelling(IReadOnlyDictionary<RelativePath, RelativePath> unified, string path) => unified[P(path)].ToString();

    [Fact]
    public void TheFirstSpellingOfAPathWins()
    {
        var unified = PathCasing.Unify([P("Mods/Cool/Meshes/a.nif"), P("mods/cool/meshes/b.nif"), P("MODS/COOL/a.esp")]);

        Spelling(unified, "mods/cool/meshes/b.nif").Should().Be("Mods/Cool/Meshes/b.nif");
        Spelling(unified, "MODS/COOL/a.esp").Should().Be("Mods/Cool/a.esp");
        Spelling(unified, "Mods/Cool/Meshes/a.nif").Should().Be("Mods/Cool/Meshes/a.nif");
    }

    [Fact]
    public void TwoSpellingsOfOneFile_BecomeOne()
    {
        var unified = PathCasing.Unify([P("Data/Skyrim.ESM"), P("data/skyrim.esm")]);

        Spelling(unified, "data/skyrim.esm").Should().Be("Data/Skyrim.ESM");
    }

    [Fact]
    public void UnrelatedPathsAreLeftAlone()
    {
        var unified = PathCasing.Unify([P("a/B.txt"), P("c/D.txt")]);

        unified.Values.Select(p => p.ToString()).Should().Equal("a/B.txt", "c/D.txt");
    }
}
