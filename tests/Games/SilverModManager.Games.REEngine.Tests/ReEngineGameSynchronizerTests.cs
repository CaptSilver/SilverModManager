using FluentAssertions;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.REEngine.Tests;

public class ReEngineGameSynchronizerTests
{
    [Theory]
    [InlineData("re2_framework_log.txt", true)]
    [InlineData("re2_framework_log.prev.txt", true)]
    [InlineData("RE2_Framework_Log.txt", true)]
    [InlineData("reframework/autorun/mod.lua", false)]
    [InlineData("reframework/data/re2_framework_log.txt", false)]
    [InlineData("dinput8.dll", false)]
    [InlineData("re2.exe", false)]
    public void TheFilterIgnoresTheLogREFrameworkWritesWhileTheGameRuns(string path, bool ignored)
    {
        ReEngineGameSynchronizer.IsRuntimeOutput(new GamePath(LocationId.Game, path)).Should().Be(ignored);
    }

    [Fact]
    public void TheFilterOnlyLooksAtTheGameFolder()
    {
        ReEngineGameSynchronizer.IsRuntimeOutput(new GamePath(LocationId.Saves, "re2_framework_log.txt")).Should().BeFalse();
    }
}
