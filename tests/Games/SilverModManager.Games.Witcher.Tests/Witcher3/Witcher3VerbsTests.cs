using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Games.Witcher.Witcher3.SortOrder;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Witcher.Tests.Witcher3;

public class Witcher3VerbsTests(ITestOutputHelper helper)
    : AIsolatedGameTest<Witcher3VerbsTests, Witcher3Game>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddWitcherGames()
        .AddUniversalGameLocator<Witcher3Game>(new Version("4.0"), locationsFileSystem: IsolatedKnownFolders.Create);

    private sealed class RecordingRenderer : IRenderer
    {
        public List<string> Lines { get; } = [];

        public ValueTask RenderAsync(IRenderable renderable)
        {
            if (renderable is Text text) Lines.Add(text.Template);
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync() => ValueTask.CompletedTask;
    }

    private WitcherModSortOrderVariety Variety =>
        Game.SortOrderManager.GetSortOrderVarieties().OfType<WitcherModSortOrderVariety>().Single();

    private async Task AddMod(Loadout.ReadOnly loadout, string name, ulong priority, string path)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, path), path);
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = group, Priority = ConflictPriority.From(priority) };
        await tx.Commit();
    }

    [Fact]
    public async Task ModsSettings_PrintsTheRenderedFile()
    {
        var loadout = await CreateLoadout();
        await Variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId);
        await AddMod(loadout, "A", 2, "Mods/modA/a.ws");
        await AddMod(loadout, "B", 1, "Mods/modB/b.ws");
        loadout = Loadout.Load(Connection.Db, loadout.LoadoutId);
        var renderer = new RecordingRenderer();

        var exit = await Witcher3Verbs.ModsSettings(renderer, loadout);

        var expected = ModsSettingsDocument.Render(Variety.StoredOrder(loadout));
        expected.Should().Contain("[modA]").And.Contain("[modB]");
        exit.Should().Be(0);
        string.Concat(renderer.Lines).Should().Be(expected);
    }
}
