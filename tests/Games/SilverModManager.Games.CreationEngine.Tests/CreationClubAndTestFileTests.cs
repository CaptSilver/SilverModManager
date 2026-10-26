using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Games.CreationEngine.Emitters;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.CreationEngine.Tests;

/// <summary>Early-loading plugins and the sTestFile warning on Fallout 4, with fixture files in a throwaway prefix.</summary>
public class Fallout4EarlyLoadingTests(ITestOutputHelper helper)
    : AIsolatedGameTest<Fallout4EarlyLoadingTests, CreationEngine.Fallout4.Fallout4>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.Fallout4.Fallout4>(new Version("1.10.163"), locationsFileSystem: IsolatedKnownFolders.Create);

    private async Task Write(GamePath path, string text)
    {
        var file = GameInstallation.Locations.ToAbsolutePath(path);
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(text);
    }

    private async Task<List<Diagnostic>> Diagnose()
    {
        var loadout = await CreateLoadout();
        var found = new List<Diagnostic>();
        await foreach (var diagnostic in new TestFileEntriesEmitter(Game).Diagnose(loadout, CancellationToken.None))
            found.Add(diagnostic);
        return found;
    }

    [Fact]
    public void WithoutAListFile_OnlyTheHardcodedPluginsLoadEarly() =>
        Game.EarlyLoadingPlugins(GameInstallation).Should().Equal(Game.HardcodedPlugins);

    [Fact]
    public async Task ListedClubPluginsFollowTheHardcodedOnes_WithoutDuplicates()
    {
        await Write(new GamePath(LocationId.Game, "Fallout4.ccc"), "ccBGSFO4001-PipBoy(Black).esl\n\nfallout4.ESM\nccBGSFO4002-PipBoy(Blue).esl\r\nccbgsfo4001-pipboy(black).esl\n");

        Game.EarlyLoadingPlugins(GameInstallation).Should().Equal(
            [.. Game.HardcodedPlugins, "ccBGSFO4001-PipBoy(Black).esl", "ccBGSFO4002-PipBoy(Blue).esl"]);
    }

    [Fact]
    public async Task NoTestFileEntry_NoWarning()
    {
        await Write(new GamePath(LocationId.Preferences, "Fallout4.ini"), "[General]\nsStartingConsoleCommand=cow\n");

        (await Diagnose()).Should().BeEmpty();
    }

    [Fact]
    public async Task NoIniAtAll_NoWarning() => (await Diagnose()).Should().BeEmpty();

    [Fact]
    public async Task ATestFileEntry_WarnsAndNamesTheKey()
    {
        await Write(new GamePath(LocationId.Preferences, "Fallout4.ini"), "[General]\nsTestFile1=Foo.esp\n");

        var diagnostic = (await Diagnose()).Should().ContainSingle().Subject;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.FormatSummary(new PlainDiagnosticWriter()).Should().Contain("sTestFile1");
    }

    [Fact]
    public async Task TheCustomIniOverridesTheBaseOneKeyByKey()
    {
        await Write(new GamePath(LocationId.Preferences, "Fallout4.ini"), "[General]\nsTestFile1=Foo.esp\n");
        await Write(new GamePath(LocationId.Preferences, "Fallout4Custom.ini"), "[General]\nsTestFile1=\nsTestFile10=Bar.esp\n");

        var diagnostic = (await Diagnose()).Should().ContainSingle().Subject;

        diagnostic.FormatSummary(new PlainDiagnosticWriter()).Should().Contain("sTestFile10").And.NotMatchRegex("sTestFile1(?!0)");
    }

    [Fact]
    public async Task EntriesOutsideTheGeneralSectionAndEmptyValuesAreIgnored()
    {
        await Write(new GamePath(LocationId.Preferences, "Fallout4.ini"), "[Other]\nsTestFile1=Foo.esp\n[General]\nsTestFile2=\n");

        (await Diagnose()).Should().BeEmpty();
    }

    [Fact]
    public async Task ACommentedOutEntryDoesNotWarnButAnEntryWithATrailingCommentDoes()
    {
        await Write(new GamePath(LocationId.Preferences, "Fallout4.ini"),
            "[General]\r\n;sTestFile1=Foo.esp\r\nsTestFile2=Bar.esp ; for testing\r\n");

        var diagnostic = (await Diagnose()).Should().ContainSingle().Subject;

        diagnostic.FormatSummary(new PlainDiagnosticWriter()).Should().Contain("sTestFile2").And.NotContain("sTestFile1");
    }

    [Fact]
    public async Task AnIniWithWindows1252BytesIsStillRead()
    {
        var file = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Preferences, "Fallout4.ini"));
        file.Parent.CreateDirectory();
        await file.WriteAllBytesAsync(System.Text.Encoding.Latin1.GetBytes("[General]\r\nsPlayerName=Ren\u00e9e\r\nsTestFile4=Foo.esp\r\n"));

        (await Diagnose()).Should().ContainSingle().Which.FormatSummary(new PlainDiagnosticWriter()).Should().Contain("sTestFile4");
    }
}

public class StarfieldEarlyLoadingTests(ITestOutputHelper helper)
    : AIsolatedGameTest<StarfieldEarlyLoadingTests, CreationEngine.Starfield.Starfield>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.Starfield.Starfield>(new Version("1.15.222"), locationsFileSystem: IsolatedKnownFolders.Create);

    private async Task Write(GamePath path, string text)
    {
        var file = GameInstallation.Locations.ToAbsolutePath(path);
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(text);
    }

    [Fact]
    public async Task TheGameFolderListIsUsedWhenMyGamesHasNone()
    {
        await Write(new GamePath(LocationId.Game, "Starfield.ccc"), "ccGame.esm\n");

        Game.EarlyLoadingPlugins(GameInstallation).Should().Equal([.. Game.HardcodedPlugins, "ccGame.esm"]);
    }

    [Fact]
    public async Task TheMyGamesListReplacesTheGameFolderOne()
    {
        await Write(new GamePath(LocationId.Game, "Starfield.ccc"), "ccGame.esm\n");
        await Write(new GamePath(LocationId.Preferences, "Starfield.ccc"), "ccMyGames.esm\n");

        Game.EarlyLoadingPlugins(GameInstallation).Should().Equal([.. Game.HardcodedPlugins, "ccMyGames.esm"]);
    }

    [Fact]
    public async Task AnEmptyMyGamesListStillOverridesTheGameFolderOne()
    {
        await Write(new GamePath(LocationId.Game, "Starfield.ccc"), "ccGame.esm\n");
        await Write(new GamePath(LocationId.Preferences, "Starfield.ccc"), "");

        Game.EarlyLoadingPlugins(GameInstallation).Should().Equal(Game.HardcodedPlugins);
    }

    [Fact]
    public async Task ATestFileEntryInTheGameFolderIniWarns()
    {
        await Write(new GamePath(LocationId.Game, "Starfield.ini"), "[General]\nsTestFile3=Foo.esp\n");
        var loadout = await CreateLoadout();

        var found = new List<Diagnostic>();
        await foreach (var diagnostic in new TestFileEntriesEmitter(Game).Diagnose(loadout, CancellationToken.None))
            found.Add(diagnostic);

        found.Should().ContainSingle().Which.FormatSummary(new PlainDiagnosticWriter()).Should().Contain("sTestFile3");
    }
}
