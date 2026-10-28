using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Games.Witcher.Witcher3.Diagnostics;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Witcher.Tests.Witcher3.Diagnostics;

public class MenuXmlOverwriteEmitterTests(ITestOutputHelper helper)
    : ALoadoutDiagnosticEmitterTest<MenuXmlOverwriteEmitterTests, Witcher3Game, MenuXmlOverwriteEmitter>(helper)
{
    private const string Pc = "bin/config/r4game/user_config_matrix/pc";

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddWitcherGames()
        .AddUniversalGameLocator<Witcher3Game>(new Version("4.0"), locationsFileSystem: IsolatedKnownFolders.Create);

    private Task<LoadoutItemGroupId> AddMenuMod(LoadoutId loadout, string mod, ulong priority, string xml, string content = "<UserConfig/>") =>
        Witcher3Fixtures.AddMod(Connection, FileStore, loadout, mod, priority, ($"{Pc}/{xml}", content));

    [Fact]
    public async Task AModsCopyOfANativeXml_GetsACard()
    {
        var loadout = await CreateLoadout();
        await AddMenuMod(loadout, "modA", 1, "input.xml");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        var summary = diagnostic.FormatSummary(new PlainDiagnosticWriter());
        summary.Should().Contain("the game's input.xml").And.Contain("modA").And.Contain("ships a whole input.xml");
    }

    [Fact]
    public async Task TwoModsWithTheSameXml_CardTheLoser()
    {
        var loadout = await CreateLoadout();
        await AddMenuMod(loadout, "modA", 1, "sdl.xml", "<A/>");
        await AddMenuMod(loadout, "modB", 2, "sdl.xml", "<B/>");

        var diagnostic = await GetSingleDiagnostic(loadout);

        var text = diagnostic.FormatSummary(new PlainDiagnosticWriter());
        text.Should().Contain("modA's sdl.xml").And.Contain("modB loads after it (lower in the Load Order list), so its copy is used");
        diagnostic.FormatDetails(new PlainDiagnosticWriter()).Should().Contain("Load Order page");
    }

    [Fact]
    public async Task AUniqueModXml_GetsNoCard()
    {
        var loadout = await CreateLoadout();
        await AddMenuMod(loadout, "modA", 1, "sdl.xml");

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task ADisabledModsXml_GetsNoCard()
    {
        var loadout = await CreateLoadout();
        await AddMenuMod(loadout, "modA", 1, "sdl.xml");
        var disabled = await AddMenuMod(loadout, "modB", 2, "sdl.xml");
        await DisableItem(disabled.Value);

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task ANativeXmlShippedByTwoMods_CardsBothTheNativeOverwriteAndTheLoser()
    {
        var loadout = await CreateLoadout();
        await AddMenuMod(loadout, "modA", 1, "hud.xml");
        await AddMenuMod(loadout, "modB", 2, "hud.xml");

        var cards = await GetAllDiagnostics(loadout);

        cards.Should().HaveCount(3, "one native overwrite per mod and one loser");
    }
}
