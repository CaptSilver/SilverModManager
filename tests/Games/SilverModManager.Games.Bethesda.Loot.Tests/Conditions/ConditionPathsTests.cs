using SilverModManager.Games.Bethesda.Loot.Conditions;
using SilverModManager.Games.Bethesda.Loot.Masterlists;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Conditions;

public class ConditionPathsTests : IDisposable
{
    private readonly TempDirectory _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public void Resolve_ReturnsTheExactJoinWhenItExists()
    {
        var file = _data.Write("Skyrim.esm");

        ConditionPaths.Resolve(_data.Path, "Skyrim.esm", tryGhost: false).Should().Be(file);
    }

    [Fact]
    public void Resolve_FindsAFileByDifferentCase()
    {
        var file = _data.Write("Skyrim.esm");

        ConditionPaths.Resolve(_data.Path, "SKYRIM.ESM", tryGhost: false).Should().Be(file);
    }

    [Fact]
    public void Resolve_FindsEachFolderOfAPathByDifferentCase()
    {
        var file = _data.Write("SKSE/Plugins/Thing.dll");

        ConditionPaths.Resolve(_data.Path, "skse/plugins/thing.dll", tryGhost: false).Should().Be(file);
    }

    [Fact]
    public void Resolve_PrefersTheExactNameWhenTwoDifferOnlyInCase()
    {
        _data.Write("a.esp");
        var exact = _data.Write("A.esp");

        ConditionPaths.Resolve(_data.Path, "A.esp", tryGhost: false).Should().Be(exact);
    }

    [Fact]
    public void Resolve_FindsAGhostedPlugin()
    {
        var ghost = _data.Write("Mod.esp.ghost");

        ConditionPaths.Resolve(_data.Path, "mod.esp", tryGhost: true).Should().Be(ghost);
    }

    [Fact]
    public void Resolve_DoesNotTryTheGhostWhenNotAsked()
    {
        _data.Write("Mod.esp.ghost");

        System.IO.File.Exists(ConditionPaths.Resolve(_data.Path, "Mod.esp", tryGhost: false)).Should().BeFalse();
    }

    [Fact]
    public void Resolve_PrefersTheUnghostedFile()
    {
        _data.Write("Mod.esp.ghost");
        var plain = _data.Write("Mod.esp");

        ConditionPaths.Resolve(_data.Path, "Mod.esp", tryGhost: true).Should().Be(plain);
    }

    [Fact]
    public void Resolve_ReturnsThePathItSettledOnWhenNothingExists()
    {
        ConditionPaths.Resolve(_data.Path, "Missing.esp", tryGhost: false)
            .Should().Be(Path.Combine(_data.Path, "Missing.esp"));
        ConditionPaths.Resolve(_data.Path, "Missing.esp", tryGhost: true)
            .Should().Be(Path.Combine(_data.Path, "Missing.esp.ghost"));
    }

    [Fact]
    public void Resolve_FindsADirectory()
    {
        var dir = _data.CreateDirectory("Meshes");

        ConditionPaths.Resolve(_data.Path, "MESHES", tryGhost: false).Should().Be(dir);
    }

    [Theory]
    [InlineData(LootGame.SkyrimSE, "A.esp", true)]
    [InlineData(LootGame.SkyrimSE, "A.ESM", true)]
    [InlineData(LootGame.SkyrimSE, "A.esl", true)]
    [InlineData(LootGame.Skyrim, "A.esl", false)]
    [InlineData(LootGame.Oblivion, "A.esl", false)]
    [InlineData(LootGame.SkyrimSE, "A.dll", false)]
    [InlineData(LootGame.SkyrimSE, "A.esp.ghost", false)]
    [InlineData(LootGame.OpenMW, "A.omwaddon", true)]
    [InlineData(LootGame.OpenMW, "A.omwgame", true)]
    [InlineData(LootGame.OpenMW, "A.omwscripts", true)]
    [InlineData(LootGame.SkyrimSE, "A.omwaddon", false)]
    [InlineData(LootGame.SkyrimSE, "noextension", false)]
    public void ShouldTryGhost_OnlyForPluginExtensionsTheGameHas(LootGame game, string path, bool expected)
    {
        // OpenMW never ghosts, so it is false there whatever the extension.
        ConditionPaths.ShouldTryGhost(game, path).Should().Be(expected && game != LootGame.OpenMW);
    }

    [Theory]
    [InlineData(LootGame.SkyrimSE, "A.esp", true)]
    [InlineData(LootGame.SkyrimSE, "A.esp.ghost", true)]
    [InlineData(LootGame.SkyrimSE, "A.ESP.GHOST", true)]
    [InlineData(LootGame.SkyrimSE, "A.dll.ghost", false)]
    [InlineData(LootGame.SkyrimSE, "A.ghost", false)]
    [InlineData(LootGame.Skyrim, "A.esl.ghost", false)]
    [InlineData(LootGame.OpenMW, "A.esp.ghost", false)]
    [InlineData(LootGame.OpenMW, "A.omwaddon", true)]
    public void HasPluginExtension_AllowsAGhostSuffixWhereGhostingIsAllowed(LootGame game, string path, bool expected)
    {
        ConditionPaths.HasPluginExtension(game, path).Should().Be(expected);
    }

    [Theory]
    [InlineData(LootGame.SkyrimSE, "A.esp.ghost", "A.esp")]
    [InlineData(LootGame.SkyrimSE, "A.ESM.Ghost", "A.ESM")]
    [InlineData(LootGame.SkyrimSE, "A.dll.ghost", "A.dll.ghost")]
    [InlineData(LootGame.SkyrimSE, "A.esp", "A.esp")]
    [InlineData(LootGame.OpenMW, "A.esp.ghost", "A.esp.ghost")]
    public void NormaliseFileName_StripsGhostOffPluginNames(LootGame game, string name, string expected)
    {
        ConditionPaths.NormaliseFileName(game, name).Should().Be(expected);
    }

    [Fact]
    public void ListDirectory_YieldsFilesAndFoldersWithGhostStrippedOffPlugins()
    {
        _data.Write("Textures/a.esp.ghost");
        _data.Write("Textures/b.dll.ghost");
        _data.Write("Textures/c.dds");
        _data.CreateDirectory("Textures/Sub");

        ConditionPaths.ListDirectory(_data.Path, "textures", LootGame.SkyrimSE)
            .Should().BeEquivalentTo("a.esp", "b.dll.ghost", "c.dds", "Sub");
    }

    [Fact]
    public void ListDirectory_OfTheDataFolderItself()
    {
        _data.Write("a.esp");

        ConditionPaths.ListDirectory(_data.Path, ".", LootGame.SkyrimSE).Should().Equal("a.esp");
    }

    [Fact]
    public void ListDirectory_IsEmptyForAMissingParentOrAFile()
    {
        _data.Write("file.txt");

        ConditionPaths.ListDirectory(_data.Path, "missing", LootGame.SkyrimSE).Should().BeEmpty();
        ConditionPaths.ListDirectory(_data.Path, "file.txt", LootGame.SkyrimSE).Should().BeEmpty();
    }
}
