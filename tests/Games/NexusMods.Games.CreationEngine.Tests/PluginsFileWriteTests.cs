using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.Creations;
using NexusMods.Games.CreationEngine.PluginLists;
using NexusMods.Games.CreationEngine.SortOrder;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NSubstitute;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests;

/// <summary>
/// What the plugin list file says about the plugins the game installed itself: the group's switch decides them, the
/// plugin's own sort row does not, and a Creation whose header can't be read is still listed.
/// </summary>
public class PluginsFileWriteTests
{
    private static readonly GamePath DataFolder = new(LocationId.Game, "Data");

    private const string Listed = "ccinvented001-garden.esm";
    private const string Unlisted = "ccinvented002-shed.esm";

    private static ICreationEngineGame Game(params string[] unreadable)
    {
        var game = Substitute.For<ICreationEngineGame>();
        game.DataPath.Returns(DataFolder);
        game.PluginsFile.Returns(new GamePath(LocationId.AppData, "Plugins.txt"));
        game.ParsePlugin(Hash.From(1), null).ReturnsForAnyArgs(call =>
        {
            var name = call.ArgAt<RelativePath?>(1)!.Value.FileName.ToString();
            PluginHeader? header = unreadable.Contains(name)
                ? null
                : new PluginHeader(name, [], 0, null, null, null, null, PluginFormat.Tes4);
            return ValueTask.FromResult(header);
        });
        game.IsMaster(default!, default!).ReturnsForAnyArgs(call =>
            call.ArgAt<PluginHeader>(0).FileName.EndsWith(".esm", StringComparison.OrdinalIgnoreCase));
        return game;
    }

    private static Dictionary<GamePath, SyncNode> Tree(params string[] names) => names.ToDictionary(
        name => new GamePath(LocationId.Game, "Data/" + name),
        _ => new SyncNode
        {
            Loadout = new SyncNodePart { Hash = Hash.From(1), Size = NexusMods.Paths.Size.From(10), LastModifiedTicks = 0 },
            SourceItemType = LoadoutSourceItemType.Game,
        });

    private static PluginSortData Row(string name, int index, bool enabled = true) => new(name, index, enabled);

    private static GameInstalledPlugins Installed(bool groupEnabled, params string[] names) =>
        new(names.ToHashSet(PluginNameComparer.Instance), groupEnabled);

    private static async Task<string[]> Lines(
        ICreationEngineGame game,
        string[] tree,
        PluginSortData[] rows,
        GameInstalledPlugins installed,
        params string[] earlyLoaders)
    {
        var file = new PluginsFile(NullLogger<PluginsFile>.Instance, game, new AsteriskMechanism());
        using var stream = new MemoryStream();
        await file.Render(stream, null!, earlyLoaders, rows, Tree(tree), installed);
        return Encoding.UTF8.GetString(stream.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .ToArray();
    }

    [Fact]
    public async Task WithTheGroupOn_ACreationIsStarred_EvenWhenItsOwnRowIsOff()
    {
        var lines = await Lines(Game(), ["Mod.esp", Unlisted], [Row("Mod.esp", 0), Row(Unlisted, 1, enabled: false)], Installed(true, Unlisted));

        // A master loads before a plain plugin whatever its row says.
        lines.Should().Equal("*" + Unlisted, "*Mod.esp");
    }

    [Fact]
    public async Task WithTheGroupOff_ACreationThatIsNotListedInTheCccFileIsUnstarred_EvenWhenItsOwnRowIsOn()
    {
        var lines = await Lines(Game(), ["Mod.esp", Unlisted], [Row("Mod.esp", 0), Row(Unlisted, 1, enabled: true)], Installed(false, Unlisted));

        lines.Should().Equal(Unlisted, "*Mod.esp");
    }

    [Fact]
    public async Task WithTheGroupOff_ACreationInTheCccFileGetsAnExplicitUnstarredLine_AndOtherEarlyLoadersStayOut()
    {
        var lines = await Lines(Game(), ["Skyrim.esm", Listed, "Mod.esp"], [Row("Mod.esp", 0)], Installed(false, Listed), "Skyrim.esm", Listed);

        lines.Should().Equal(Listed, "*Mod.esp");
    }

    [Fact]
    public async Task WithTheGroupOn_ACreationInTheCccFileStaysOutOfTheFile()
    {
        var lines = await Lines(Game(), ["Skyrim.esm", Listed, "Mod.esp"], [Row("Mod.esp", 0)], Installed(true, Listed), "Skyrim.esm", Listed);

        lines.Should().Equal("*Mod.esp");
    }

    [Fact]
    public async Task APluginTheGameDidNotInstall_KeepsItsRowSwitchWhateverTheGroupSays()
    {
        var lines = await Lines(Game(), ["Off.esp", "On.esp"], [Row("Off.esp", 0, enabled: false), Row("On.esp", 1)], Installed(false, Unlisted));

        lines.Should().Equal("Off.esp", "*On.esp");
    }

    [Fact]
    public async Task ACreationWhoseHeaderCannotBeRead_IsWrittenAtItsIndexWithoutThrowing()
    {
        var game = Game(unreadable: Unlisted);

        var lines = await Lines(game, ["A.esp", Unlisted, "B.esp"], [Row("A.esp", 0), Row(Unlisted, 1), Row("B.esp", 2)], Installed(true, Unlisted));

        lines.Should().Equal("*A.esp", "*" + Unlisted, "*B.esp");
    }

    [Fact]
    public async Task ALoadoutPluginWhoseHeaderCannotBeRead_StillThrows()
    {
        var game = Game(unreadable: "Broken.esp");

        var act = () => Lines(game, ["A.esp", "Broken.esp"], [Row("A.esp", 0)], Installed(true, Unlisted));

        await act.Should().ThrowAsync<UnreadablePluginsException>();
    }
}
