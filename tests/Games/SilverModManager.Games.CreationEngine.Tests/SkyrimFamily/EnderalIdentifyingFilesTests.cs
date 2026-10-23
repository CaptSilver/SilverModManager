using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Tests.SkyrimFamily;

/// <summary>
/// Enderal launches TESV.exe or SkyrimSE.exe like Skyrim does and ships Skyrim.esm too, so only its own launcher
/// and master file tell the installs apart.
/// </summary>
public class EnderalIdentifyingFilesTests(IServiceProvider provider)
{
    private static readonly string[] EnderalFiles = ["Enderal Launcher.exe", "Data/Enderal - Forgotten Stories.esm"];

    private IReadOnlyList<string> Files<TGame>() where TGame : IGame
        => ((IGameIdentifyingFiles)provider.GetServices<IGame>().OfType<TGame>().Single()).IdentifyingFiles.Select(file => file.ToString()).ToArray();

    [Fact]
    public void BothEnderalsNameTheirLauncherAndMasterFile()
    {
        Files<CreationEngine.Enderal.Enderal>().Should().Equal(EnderalFiles);
        Files<CreationEngine.EnderalSE.EnderalSE>().Should().Equal(EnderalFiles);
    }

    [Fact]
    public void SkyrimNamesNoneBecauseItsMasterFileIsInEnderalToo()
    {
        Files<CreationEngine.SkyrimLE.SkyrimLE>().Should().BeEmpty();
        Files<CreationEngine.SkyrimSE.SkyrimSE>().Should().BeEmpty();
    }
}
