using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.Larian.Tests.BaldursGate3;

public class BG3GameWrittenPathTests(IServiceProvider serviceProvider)
{
    private readonly ILoadoutSynchronizer _synchronizer = serviceProvider
        .GetServices<IGame>().OfType<Larian.BaldursGate3.BaldursGate3>().Single().Synchronizer;

    [Theory]
    [InlineData("bin/gold.2024-05-01.log")]
    [InlineData("bin/network.2024-05-01.log")]
    [InlineData("bin/CrashDump - 2024-05-01 12-00-00.dmp")]
    [InlineData("Launcher/debug.log")]
    [InlineData("bin/NativeModLoader.log")]
    [InlineData("bin/NativeMods/SomeMod.log")]
    public void LogsAndCrashDumps_AreWrittenByTheGame(string path)
    {
        _synchronizer.IsGameWrittenPath(new GamePath(LocationId.Game, path)).Should().BeTrue();
    }

    [Theory]
    [InlineData("bin/NativeMods/SomeMod.dll")]
    [InlineData("bin/NativeMods/SomeMod.json")]
    [InlineData("bin/bg3.exe")]
    [InlineData("Data/Mods/Some.pak")]
    public void ModPayloads_AreNot(string path)
    {
        _synchronizer.IsGameWrittenPath(new GamePath(LocationId.Game, path)).Should().BeFalse();
    }
}
