using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Tests;

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
        { typeof(CreationEngine.Oblivion.Oblivion), "oblivion" },
        { typeof(CreationEngine.Fallout3.Fallout3), "fallout3" },
        { typeof(CreationEngine.FalloutNewVegas.FalloutNewVegas), "newvegas" },
        { typeof(CreationEngine.Morrowind.Morrowind), "morrowind" },
        { typeof(CreationEngine.OblivionRemastered.OblivionRemastered), "oblivionremastered" },
        { typeof(CreationEngine.Fallout4London.Fallout4London), "fallout4london" },
        { typeof(CreationEngine.Nehrim.Nehrim), "nehrim" },
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
    public void SteamAndGogIdsAreUniqueAcrossTheCatalog_UnlessTheirFilesTellTheGamesApart()
    {
        var titles = Titles.Select(t => _games.Single(g => g.GetType() == t.Data.Item1)).ToArray();

        // A shared id is allowed when no two claimants name the same identifying files: a folder then fits
        // at most one of the games that name files, and the one naming none is what is left.
        foreach (var ids in new[] { SteamIds(titles), GogIds(titles) })
        {
            foreach (var shared in ids.GroupBy(claim => claim.Id).Where(group => group.Count() > 1))
            {
                var declared = shared.Select(claim => string.Join('|', FilesOf(claim.Game).Order())).ToArray();
                declared.Should().OnlyHaveUniqueItems($"{shared.Key} is shared by {string.Join(", ", shared.Select(claim => claim.Game.GameId))}");
            }
        }
    }

    private static IEnumerable<(long Id, IGameData Game)> SteamIds(IEnumerable<IGameData> games)
        => games.SelectMany(game => game.StoreIdentifiers.SteamAppIds.Select(id => ((long)id, game)));

    private static IEnumerable<(long Id, IGameData Game)> GogIds(IEnumerable<IGameData> games)
        => games.SelectMany(game => game.StoreIdentifiers.GOGProductIds.Select(id => (id, game)));

    private static IEnumerable<string> FilesOf(IGameData game)
        => (game as IGameIdentifyingFiles)?.IdentifyingFiles.Select(file => file.ToString().ToLowerInvariant()) ?? [];

    [Fact]
    public void SkyrimLEAndEnderalShareAnExecutable() => PrimaryExeOf<CreationEngine.SkyrimLE.SkyrimLE>()
        .Should().Be(PrimaryExeOf<CreationEngine.Enderal.Enderal>()).And.Be("TESV.exe");

    [Fact]
    public void SkyrimSEAndEnderalSEShareAnExecutable() => PrimaryExeOf<CreationEngine.SkyrimSE.SkyrimSE>()
        .Should().Be(PrimaryExeOf<CreationEngine.EnderalSE.EnderalSE>()).And.Be("SkyrimSE.exe");

    private string PrimaryExeOf<TGame>() where TGame : class, IGame
        => _games.OfType<TGame>().Single().GetPrimaryFile(default!).Path.ToString();
}
