using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Games.Witcher.Witcher3.Diagnostics;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Witcher.Tests.Witcher3.Diagnostics;

public class DroppedSettingsPartEmitterTests(ITestOutputHelper helper)
    : ALoadoutDiagnosticEmitterTest<DroppedSettingsPartEmitterTests, Witcher3Game, DroppedSettingsPartEmitter>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddWitcherGames()
        .AddUniversalGameLocator<Witcher3Game>(new Version("4.0"), locationsFileSystem: IsolatedKnownFolders.Create);

    private Task<LoadoutItemGroupId> AddPart(LoadoutId loadout, string mod, string target) =>
        Witcher3Fixtures.AddMod(Connection, FileStore, loadout, mod, 1, ($"INI Tweaks/{mod} [{target}].ini", "[SDL]\nIK_F=1\n"));

    [Fact]
    public async Task APartForAnUnknownSettingsFile_GetsACard()
    {
        var loadout = await CreateLoadout();
        await AddPart(loadout, "modA", "inputt.settings");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        var summary = diagnostic.FormatSummary(new PlainDiagnosticWriter());
        summary.Should().Contain("modA").And.Contain("inputt.settings")
            .And.Contain("reads only input.settings, user.settings and dx12user.settings");
    }

    [Fact]
    public async Task AnInputXmlPart_GetsTheXmlCard()
    {
        var loadout = await CreateLoadout();
        await AddPart(loadout, "modA", "input.xml");

        var diagnostic = await GetSingleDiagnostic(loadout);

        var summary = diagnostic.FormatSummary(new PlainDiagnosticWriter());
        summary.Should().Contain("modA").And.Contain("input.xml").And.Contain("XML fragment");
    }

    [Theory]
    [InlineData("input.settings")]
    [InlineData("user.settings")]
    [InlineData("DX12USER.SETTINGS")]
    public async Task APartForAKnownFile_GetsNoCard(string target)
    {
        var loadout = await CreateLoadout();
        var file = GameInstallation.Locations.ToAbsolutePath(new GamePath(WitcherConstants.WitcherDocuments, target.ToLowerInvariant()));
        file.Parent.CreateDirectory();
        await file.WriteAllBytesAsync("[Rendering]\r\nMaxFPS=30\r\n"u8.ToArray());
        await AddPart(loadout, "modA", target);

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task ACardNamesTheModOnce()
    {
        var loadout = await CreateLoadout();
        await AddPart(loadout, "modA", "inputt.settings");

        var summary = (await GetSingleDiagnostic(loadout)).FormatSummary(new PlainDiagnosticWriter());

        summary.Should().StartWith("the inputt.settings fragment from modA is not used");
    }

    [Fact]
    public async Task TheCardsReasonCitesNoSourceFile()
    {
        var loadout = await CreateLoadout();
        await AddPart(loadout, "modA", "input.xml");

        var summary = (await GetSingleDiagnostic(loadout)).FormatSummary(new PlainDiagnosticWriter());

        summary.Should().NotContain(".ts").And.Contain("only merges .settings files");
    }

    [Fact]
    public async Task APartForASettingsFileThatDoesNotExistYet_GetsOneCardNamingTheMods()
    {
        var loadout = await CreateLoadout();
        await AddPart(loadout, "modA", "dx12user.settings");
        await AddPart(loadout, "modB", "dx12user.settings");

        var summary = (await GetSingleDiagnostic(loadout)).FormatSummary(new PlainDiagnosticWriter());

        summary.Should().Contain("modA").And.Contain("modB").And.Contain("dx12user.settings doesn't exist yet")
            .And.Contain("Start the game once, then Apply again");
    }

    [Fact]
    public async Task APartForASettingsFileOnDisk_GetsNoCard()
    {
        var loadout = await CreateLoadout();
        var path = GameInstallation.Locations.ToAbsolutePath(new GamePath(WitcherConstants.WitcherDocuments, "user.settings"));
        path.Parent.CreateDirectory();
        await path.WriteAllBytesAsync("[Rendering]\r\nMaxFPS=30\r\n"u8.ToArray());
        await AddPart(loadout, "modA", "user.settings");

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task APartOfADisabledMod_GetsNoCard()
    {
        var loadout = await CreateLoadout();
        var group = await AddPart(loadout, "modA", "inputt.settings");
        await DisableItem(group.Value);

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Theory]
    [InlineData("inputt.settings")]
    [InlineData("input.xml")]
    public async Task APartForAFileTheGameDoesNotRead_IsNeverBakedAndIsReported(string target)
    {
        var loadout = await CreateLoadout();
        var inputSettings = new GamePath(WitcherConstants.WitcherDocuments, "input.settings");
        GameInstallation.Locations.ToAbsolutePath(inputSettings).Parent.CreateDirectory();
        await GameInstallation.Locations.ToAbsolutePath(inputSettings).WriteAllBytesAsync("[Exploration]\r\nIK_E=1\r\n"u8.ToArray());
        await AddPart(loadout, "modA", target);

        await Synchronizer.Synchronize(Refresh(Loadout.Load(Connection.Db, loadout)));

        GameInstallation.Locations.ToAbsolutePath(new GamePath(WitcherConstants.WitcherDocuments, target)).FileExists.Should().BeFalse();
        IniDocument.Parse(await GameInstallation.Locations.ToAbsolutePath(inputSettings).ReadAllBytesAsync()).Get("SDL", "IK_F")
            .Should().BeNull("the fragment named another file");
        var card = await GetSingleDiagnostic(loadout);
        card.FormatDetails(new PlainDiagnosticWriter()).Should().Contain("was left out").And.Contain(target);
    }
}
