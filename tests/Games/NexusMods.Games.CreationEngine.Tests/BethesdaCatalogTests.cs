using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Tests;

/// <summary>
/// What every Bethesda title registered by AddCreationEngine has to get right: a unique game id and store
/// ids, and a Nexus id that is the one games.json lists for the title's domain.
/// </summary>
public class BethesdaCatalogTests
{
    // (game type, Nexus domain). Extended as titles are added.
    public static TheoryData<Type, string> Titles => new()
    {
        { typeof(CreationEngine.SkyrimSE.SkyrimSE), "skyrimspecialedition" },
        { typeof(CreationEngine.Fallout4.Fallout4), "fallout4" },
        { typeof(CreationEngine.SkyrimLE.SkyrimLE), "skyrim" },
        // Skyrim VR has no Nexus domain of its own; its mods are on Skyrim SE's.
        { typeof(CreationEngine.SkyrimVR.SkyrimVR), "skyrimspecialedition" },
        { typeof(CreationEngine.Enderal.Enderal), "enderal" },
        { typeof(CreationEngine.EnderalSE.EnderalSE), "enderalspecialedition" },
        // Fallout 4 VR has no Nexus domain of its own; its mods are on Fallout 4's.
        { typeof(CreationEngine.Fallout4VR.Fallout4VR), "fallout4" },
        { typeof(CreationEngine.Fallout76.Fallout76), "fallout76" },
        { typeof(CreationEngine.Starfield.Starfield), "starfield" },
    };

    private readonly IGameData[] _games;
    private static readonly Lazy<Dictionary<string, uint>> NexusIds = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "games.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        // Some domains (neverwinter) are listed twice; the Bethesda ones are not, so the first entry is enough.
        var ids = new Dictionary<string, uint>();
        foreach (var e in doc.RootElement.EnumerateArray())
            ids.TryAdd(e.GetProperty("domain_name").GetString()!, e.GetProperty("id").GetUInt32());
        return ids;
    });

    public BethesdaCatalogTests(IServiceProvider provider)
    {
        _games = provider.GetServices<IGame>().Cast<IGameData>().ToArray();
    }

    [Theory]
    [MemberData(nameof(Titles))]
    public void EveryTitleIsRegistered(Type type, string _) => _games.Select(g => g.GetType()).Should().Contain(type);

    [Theory]
    [MemberData(nameof(Titles))]
    public void NexusIdMatchesTheDomainInGamesJson(Type type, string domain)
    {
        var game = _games.Single(g => g.GetType() == type);

        NexusIds.Value.Should().ContainKey(domain);
        game.NexusModsGameId.Value.Value.Should().Be(NexusIds.Value[domain]);
    }

    [Fact]
    public void GameIdsAreUnique()
    {
        var ids = Titles.Select(t => _games.Single(g => g.GetType() == t.Data.Item1).GameId).ToArray();
        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void SteamAndGogIdsAreUniqueAcrossTheCatalog()
    {
        var titles = Titles.Select(t => _games.Single(g => g.GetType() == t.Data.Item1).StoreIdentifiers).ToArray();

        titles.SelectMany(s => s.SteamAppIds).Should().OnlyHaveUniqueItems();
        titles.SelectMany(s => s.GOGProductIds).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void SkyrimLEAndEnderalShareAnExecutable() => PrimaryExeOf<CreationEngine.SkyrimLE.SkyrimLE>()
        .Should().Be(PrimaryExeOf<CreationEngine.Enderal.Enderal>()).And.Be("TESV.exe");

    [Fact]
    public void SkyrimSEAndEnderalSEShareAnExecutable() => PrimaryExeOf<CreationEngine.SkyrimSE.SkyrimSE>()
        .Should().Be(PrimaryExeOf<CreationEngine.EnderalSE.EnderalSE>()).And.Be("SkyrimSE.exe");

    private string PrimaryExeOf<TGame>() where TGame : class, IGame
        => _games.OfType<TGame>().Single().GetPrimaryFile(default!).Path.ToString();
}
