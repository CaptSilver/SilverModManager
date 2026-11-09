using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Sdk.Tests.Games;

public class NeverLoadedFilesTests
{
    [Theory]
    [InlineData("src/skse64/main.cpp")]
    [InlineData("src/skse64/main.CPP")]
    [InlineData("a.c")]
    [InlineData("a.cc")]
    [InlineData("include/a.h")]
    [InlineData("a.hpp")]
    [InlineData("a.inl")]
    [InlineData("src/a.inc")]
    [InlineData("src/Scripts/Plugin.cs")]
    [InlineData("proj/skse.vcxproj")]
    [InlineData("proj/skse.vcproj")]
    [InlineData("proj/skse.vcxproj.filters")]
    [InlineData("proj/skse.filters")]
    [InlineData("proj/skse.sln")]
    [InlineData("res/version.rc")]
    [InlineData("build/Thing.cmake")]
    [InlineData("anywhere/CMakeLists.txt")]
    [InlineData("src/skse64/.gitignore")]
    [InlineData("Source/lib/.gitattributes")]
    [InlineData("src\\skse64\\main.cpp")]
    [InlineData("src/skse64/exports.def")]
    [InlineData("src/skse64/version.rc.in")]
    [InlineData("src/skse64/config.h.in")]
    [InlineData("src/skse64/common.props")]
    [InlineData("src/skse64/build.targets")]
    [InlineData("src/skse64/trampoline.asm")]
    [InlineData("src/skse64/types.natvis")]
    [InlineData("src/.editorconfig")]
    [InlineData("src/.clang-format")]
    public void IsNeverLoaded_IsTrueForSourceAndProjectFiles(string path)
    {
        NeverLoadedFiles.IsNeverLoaded(path).Should().BeTrue();
    }

    [Theory]
    [InlineData("SKSE/Plugins/plugin.dll")]
    [InlineData("skse64_loader.exe")]
    [InlineData("Data/Mod.esp")]
    [InlineData("Data/Mod.esm")]
    [InlineData("Data/Mod.esl")]
    [InlineData("Data/Mod.bsa")]
    [InlineData("Data/Mod.ba2")]
    [InlineData("Data/Scripts/Thing.pex")]
    [InlineData("Data/Scripts/Source/Thing.psc")]
    [InlineData("Data/Mod.ini")]
    [InlineData("Data/Mod.json")]
    [InlineData("Data/readme.txt")]
    [InlineData("src/notes.txt")]
    [InlineData(".gitignore")]
    [InlineData("Data/.gitattributes")]
    [InlineData("README.md")]
    [InlineData("Mods/Thing/Plugin.cs")]
    [InlineData("reshade-shaders/Shaders/Common.inc")]
    [InlineData("headers/chart")]
    [InlineData("a.hh")]
    [InlineData("srcs/.gitignore")]
    [InlineData("Data/exports.def")]
    [InlineData("Data/Mod.in")]
    [InlineData("common.props")]
    [InlineData("Data/.editorconfig")]
    [InlineData("")]
    public void IsNeverLoaded_IsFalseForAnythingAGameOrExtenderLoads(string path)
    {
        NeverLoadedFiles.IsNeverLoaded(path).Should().BeFalse();
    }

    [Fact]
    public void IsNeverLoaded_TakesARelativePathAndAGamePath()
    {
        NeverLoadedFiles.IsNeverLoaded(RelativePath.FromUnsanitizedInput("src/skse64/main.cpp")).Should().BeTrue();
        NeverLoadedFiles.IsNeverLoaded(new GamePath(LocationId.Game, RelativePath.FromUnsanitizedInput("src/skse64/main.cpp"))).Should().BeTrue();
        NeverLoadedFiles.IsNeverLoaded(new GamePath(LocationId.Game, RelativePath.FromUnsanitizedInput("Data/Mod.esp"))).Should().BeFalse();
    }
}
