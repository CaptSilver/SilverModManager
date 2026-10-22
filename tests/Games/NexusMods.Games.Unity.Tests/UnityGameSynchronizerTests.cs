using FluentAssertions;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.Unity.Tests;

public class UnityGameSynchronizerTests
{
    [Theory]
    [InlineData("BepInEx/LogOutput.log", true)]
    [InlineData("BepInEx/cache/chainloader_typeloader.dat", true)]
    [InlineData("BepInEx/cache/sub/file.bin", true)]
    [InlineData("BepInEx/plugins/X/X.dll", false)]
    [InlineData("BepInEx/config/BepInEx.cfg", false)]
    [InlineData("REPO.exe", false)]
    public void TheFilterIgnoresWhatBepInExWritesWhileTheGameRuns(string path, bool ignored)
    {
        UnityGameSynchronizer.IsRuntimeOutput(new GamePath(LocationId.Game, path)).Should().Be(ignored);
    }

    [Fact]
    public void TheFilterOnlyLooksAtTheGameFolder()
    {
        UnityGameSynchronizer.IsRuntimeOutput(new GamePath(LocationId.Saves, "BepInEx/LogOutput.log")).Should().BeFalse();
    }
}
