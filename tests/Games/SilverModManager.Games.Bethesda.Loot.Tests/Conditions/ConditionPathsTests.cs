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
    public void Resolve_ReadsBackslashesAsSeparatorsAndFollowsParentSegments()
    {
        _data.Write("TESV.exe");
        var data = _data.CreateDirectory("Data");

        ConditionPaths.Resolve(data, "..\\tesv.exe", tryGhost: false).Should().Be(Path.Combine(_data.Path, "TESV.exe"));
    }

    [Fact]
    public void Resolve_ReachesTheGameRootThroughAParentSegmentWithoutDroppingTheGhostRetry()
    {
        var ghost = _data.Write("Data/Mod.esp.ghost");
        var data = Path.Combine(_data.Path, "Data");

        ConditionPaths.Resolve(data, "../data/mod.esp", tryGhost: true).Should().Be(ghost);
    }

    [Fact]
    public void Resolve_ResolvesParentSegmentsInTheMiddleOfAPath()
    {
        var file = _data.Write("skse64_loader.exe");
        var data = _data.CreateDirectory("Data");

        ConditionPaths.Resolve(data, "skse/../../skse64_loader.exe", tryGhost: false).Should().Be(file);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\etc\\passwd")]
    [InlineData("skse/../../../x")]
    [InlineData("../Data/../../x")]
    [InlineData("/etc/passwd")]
    [InlineData("\\etc\\passwd")]
    [InlineData("C:\\Windows\\x")]
    public void Resolve_RefusesAPathAboveTheGameRootOrOutsideIt(string path)
    {
        _data.Write("x");
        var data = _data.CreateDirectory("Data");

        ConditionPaths.Resolve(data, path, tryGhost: true).Should().BeNull();
    }

    [Theory]
    [InlineData("A.esp", true)]
    [InlineData("A B.esp", true)]
    [InlineData("../A.esp", false)]
    [InlineData("sub/A.esp", false)]
    [InlineData("sub\\A.esp", false)]
    [InlineData("/A.esp", false)]
    [InlineData("..", false)]
    [InlineData(".", false)]
    [InlineData("", false)]
    public void IsPlainFileName_AcceptsOnlyANameWithNoSeparator(string name, bool expected) =>
        ConditionPaths.IsPlainFileName(name).Should().Be(expected);

    [Fact]
    public void IsRegularFile_TrueForAFileAndFalseForAFolderAndAMissingPath()
    {
        var file = _data.Write("a.esp");
        var folder = _data.CreateDirectory("Meshes");

        ConditionPaths.IsRegularFile(file, _data.Path).Should().BeTrue();
        ConditionPaths.IsRegularFile(folder, _data.Path).Should().BeFalse();
        ConditionPaths.IsRegularFile(Path.Combine(_data.Path, "missing.esp"), _data.Path).Should().BeFalse();
    }

    [Fact]
    public void IsRegularFile_FalseForAFifo()
    {
        var fifo = _data.CreateFifo("pipe.esp");

        ConditionPaths.IsRegularFile(fifo, _data.Path).Should().BeFalse();
    }

    [Fact]
    public void IsRegularFile_FollowsASymlinkOnlyWhileItStaysInTheGameRoot()
    {
        var data = _data.CreateDirectory("Data");
        var inside = _data.Write("real.exe");
        using var outside = new TempDirectory();
        var elsewhere = outside.Write("secret");
        System.IO.File.CreateSymbolicLink(Path.Combine(data, "in.exe"), inside);
        System.IO.File.CreateSymbolicLink(Path.Combine(data, "out"), elsewhere);

        ConditionPaths.IsRegularFile(Path.Combine(data, "in.exe"), data).Should().BeTrue();
        ConditionPaths.IsRegularFile(Path.Combine(data, "out"), data).Should().BeFalse();
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
