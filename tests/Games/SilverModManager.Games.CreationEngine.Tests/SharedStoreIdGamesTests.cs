using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Tests;

/// <summary>
/// Fallout 4 London is installed over Fallout 4 and Nehrim over Oblivion, so each pair is one store id and one
/// executable. The title that names no identifying files is the plain game; the other one names the master file
/// only its install has.
/// </summary>
public class SharedStoreIdGamesTests(IServiceProvider provider)
{
    private TGame Game<TGame>() where TGame : class, IGame => provider.GetServices<IGame>().OfType<TGame>().Single();

    private static string[] FilesOf(IGame game) => ((IGameIdentifyingFiles)game).IdentifyingFiles.Select(file => file.ToString()).ToArray();

    [Fact]
    public void Fallout4LondonNamesItsWorldSpaceMasterAndSharesFallout4sSteamId()
    {
        var london = Game<CreationEngine.Fallout4London.Fallout4London>();
        var fallout4 = Game<CreationEngine.Fallout4.Fallout4>();

        FilesOf(london).Should().Equal("Data/LondonWorldSpace.esm");
        FilesOf(fallout4).Should().BeEmpty();
        london.StoreIdentifiers.SteamAppIds.Should().Equal(377160u);
        fallout4.StoreIdentifiers.SteamAppIds.Should().Contain(377160u);
    }

    [Fact]
    public void Fallout4LondonHasItsOwnIdentityAndNexusDomain()
    {
        CreationEngine.Fallout4London.Fallout4London.GameId.Should().Be(GameId.From("CreationEngine.Fallout4London"));
        CreationEngine.Fallout4London.Fallout4London.DisplayName.Should().Be("Fallout 4 London");
        CreationEngine.Fallout4London.Fallout4London.NexusModsGameId.Value.Value.Should().Be(6332u);
    }

    [Fact]
    public void NehrimNamesItsMasterFileAndSharesOblivionsSteamId()
    {
        var nehrim = Game<CreationEngine.Nehrim.Nehrim>();
        var oblivion = Game<CreationEngine.Oblivion.Oblivion>();

        FilesOf(nehrim).Should().Equal("Data/Nehrim.esm");
        FilesOf(oblivion).Should().BeEmpty();
        nehrim.StoreIdentifiers.SteamAppIds.Should().Equal(22330u);
        nehrim.StoreIdentifiers.GOGProductIds.Should().BeEmpty();
        oblivion.StoreIdentifiers.SteamAppIds.Should().Contain(22330u);
    }

    [Fact]
    public void NehrimHasItsOwnIdentityAndNexusDomain()
    {
        CreationEngine.Nehrim.Nehrim.GameId.Should().Be(GameId.From("CreationEngine.Nehrim"));
        CreationEngine.Nehrim.Nehrim.DisplayName.Should().Be("Nehrim: At Fate's Edge");
        CreationEngine.Nehrim.Nehrim.NexusModsGameId.Value.Value.Should().Be(3312u);
    }

    [Fact]
    public void EachPairLaunchesOneExecutable()
    {
        Game<CreationEngine.Fallout4London.Fallout4London>().GetPrimaryFile(default!).Path.ToString()
            .Should().Be(Game<CreationEngine.Fallout4.Fallout4>().GetPrimaryFile(default!).Path.ToString()).And.Be("Fallout4.exe");
        Game<CreationEngine.Nehrim.Nehrim>().GetPrimaryFile(default!).Path.ToString()
            .Should().Be(Game<CreationEngine.Oblivion.Oblivion>().GetPrimaryFile(default!).Path.ToString()).And.Be("Oblivion.exe");
    }
}
