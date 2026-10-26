using System.Reactive.Linq;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Pages.Ini;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using SilverModManager.UI.Tests.Helpers;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests;

/// <summary>
/// The INI page's editor of one game INI: the loadout's copy of the file, the tweaks layered on it, the typed
/// settings, and what the bake renders from them.
/// </summary>
public class IniPageTests(IServiceProvider provider) : AUiTest(provider)
{
    private const string MainName = "SilverIniPage.ini";
    private const string PrefsName = "SilverIniPagePrefs.ini";

    private static readonly GamePath MainPath = new(LocationId.Game, MainName);
    private static readonly GamePath PrefsPath = new(LocationId.Game, PrefsName);

    private const string MainText = "[General]\r\nsLanguage=ENGLISH\r\n";
    private const string PrefsText = "; mine\r\n[Display]\r\niSize W=2560\r\niSize H=1440\r\nbFull Screen=0\r\n";

    private sealed class FakeGame : IGameIniFiles
    {
        public IniSet IniFiles { get; } = new(MainName, PrefsName);

        public GamePath IniPath(string fileName) => new(LocationId.Game, fileName);

        public GamePath? ManagedKeysFile => MainPath;

        public IReadOnlyList<IniKeyEdit> ManagedKeys(Loadout.ReadOnly loadout) => [new IniKeyEdit("Archive", "bInvalidateOlderFiles", "1")];
    }

    private readonly FakeGame _game = new();
    private readonly PresenterRig _presenter = new(provider);
    private IFileStore FileStore => Provider.GetRequiredService<IFileStore>();

    private async Task<Loadout.ReadOnly> SeedLoadout(string name = "INI") => await SeedLoadoutRow(GameStore.Steam, name);

    private Loadout.ReadOnly Fresh(Loadout.ReadOnly loadout) => Loadout.Load(Connection.Db, loadout.LoadoutId);

    private async Task<Hash> Store(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = bytes.xxHash3();
        if (!await FileStore.HaveFile(hash))
            await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory("x.ini", new MemoryStream(bytes)), hash, Size.FromLong(bytes.Length))]);
        return hash;
    }

    private async Task SeedBaseline(Loadout.ReadOnly loadout, GamePath path, string content)
    {
        var hash = await Store(content);
        using var tx = Connection.BeginTransaction();
        _ = new LoadoutIniState.New(tx)
        {
            LoadoutId = loadout.LoadoutId,
            Path = path.ToGamePathParentTuple(loadout.LoadoutId),
            Baseline = hash,
        };
        await tx.Commit();
    }

    private async Task<Loadout.ReadOnly> SeedBaselines(string name = "INI")
    {
        var loadout = await SeedLoadout(name);
        await SeedBaseline(loadout, MainPath, MainText);
        await SeedBaseline(loadout, PrefsPath, PrefsText);
        return Fresh(loadout);
    }

    private async Task<EntityId> SeedTweak(Loadout.ReadOnly loadout, string mod, string tweakFile, string content, ulong priority, bool modDisabled = false, bool settingsGroup = false)
    {
        var hash = await Store(content);
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var groupId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, groupId) { LoadoutId = loadout.LoadoutId, Name = mod },
        };
        _ = group;
        var path = new GamePath(LocationId.Game, $"INI Tweaks/{tweakFile}");
        _ = new LoadoutFile.New(tx, out var fileId)
        {
            LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, fileId)
            {
                LoadoutItem = new LoadoutItem.New(tx, fileId) { LoadoutId = loadout.LoadoutId, ParentId = groupId, Name = path.FileName },
                TargetPath = path.ToGamePathParentTuple(loadout.LoadoutId),
            },
            Hash = hash,
            Size = Size.FromLong(content.Length),
        };
        _ = new LoadoutItemGroupPriority.New(tx)
        {
            LoadoutId = loadout.LoadoutId,
            TargetId = LoadoutItemGroupId.From(groupId),
            Priority = SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts.ConflictPriority.From(priority),
        };
        if (modDisabled) tx.Add(groupId, LoadoutItem.Disabled, Null.Instance);
        if (settingsGroup) tx.Add(groupId, LoadoutItemGroup.GameSettingsGroup, Null.Instance);
        var result = await tx.Commit();
        return result[fileId];
    }

    private async Task<IniFileEditor> Editor(Loadout.ReadOnly loadout, GamePath path, IServiceProvider? services = null)
    {
        var editor = new IniFileEditor(Fresh(loadout), _game, path, services ?? _presenter.Services);
        await editor.Load();
        return editor;
    }

    private async Task<string> RenderedText(Loadout.ReadOnly loadout, GamePath path) =>
        IniDocument.Parse(await GameInis.Render(_game, FileStore, Fresh(loadout), path)).ToText();

    // ---- tabs

    [Fact]
    public async Task ThereIsOneTabPerFileOfTheIniSet_InTheOrderTheGameReadsThem()
    {
        var loadout = await SeedBaselines();

        var tabs = IniFileEditor.ForGame(loadout, _game, _presenter.Services);

        tabs.Select(t => t.FileName).Should().Equal(MainName, PrefsName);
    }

    [Fact]
    public void OnlyAGameThatKeepsItsOwnIniCopiesGetsAPage()
    {
        IniPageFactory.HasIniPage(_game).Should().BeTrue();
        IniPageFactory.HasIniPage(new object()).Should().BeFalse();
    }

    // ---- raw baseline

    [Fact]
    public async Task TheRawTabShowsTheBaselineFromTheFileStore()
    {
        var loadout = await SeedBaselines();

        var editor = await Editor(loadout, PrefsPath);

        editor.HasBaseline.Should().BeTrue();
        editor.BaselineText.Should().Be(PrefsText);
    }

    [Fact]
    public async Task AFileTheLoadoutHasNoBaselineForSaysSoAndShowsNothing()
    {
        var loadout = await SeedLoadout("No baseline");

        var editor = await Editor(loadout, PrefsPath);

        editor.HasBaseline.Should().BeFalse();
        editor.BaselineText.Should().BeEmpty();
        editor.ResultText.Should().BeEmpty("there is nothing to bake from");
    }

    [Fact]
    public async Task SavingTheRawTextStoresItAsTheNewBaseline()
    {
        var loadout = await SeedBaselines();
        var editor = await Editor(loadout, PrefsPath);
        var edited = PrefsText.Replace("2560", "1920");

        editor.BaselineText = edited;
        await editor.SaveBaselineCommand.Execute().FirstAsync();

        var state = LoadoutIniState.FindByLoadout(Connection.Db, loadout.LoadoutId).Single(s => (GamePath)s.Path == PrefsPath);
        Encoding.UTF8.GetString(await FileStore.Load(state.Baseline)).Should().Be(edited);
        editor.ResultText.Should().Contain("iSize W=1920");
    }

    [Fact]
    public async Task SavingTheRawTextKeepsTheEncodingOfTheOriginalFile()
    {
        var loadout = await SeedLoadout("Bom");
        var original = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(PrefsText)).ToArray();
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory("x.ini", new MemoryStream(original)), original.xxHash3(), Size.FromLong(original.Length))]);
        using (var tx = Connection.BeginTransaction())
        {
            _ = new LoadoutIniState.New(tx)
            {
                LoadoutId = loadout.LoadoutId,
                Path = PrefsPath.ToGamePathParentTuple(loadout.LoadoutId),
                Baseline = original.xxHash3(),
            };
            await tx.Commit();
        }
        var editor = await Editor(loadout, PrefsPath);

        editor.BaselineText = PrefsText + "[Extra]\r\nx=1\r\n";
        await editor.SaveBaselineCommand.Execute().FirstAsync();

        var state = LoadoutIniState.FindByLoadout(Connection.Db, loadout.LoadoutId).Single();
        (await FileStore.Load(state.Baseline)).Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
    }

    [Fact]
    public async Task SavingRawTextOfAFileWithoutABaselineCreatesOne()
    {
        var loadout = await SeedLoadout("Create");
        var editor = await Editor(loadout, MainPath);

        editor.BaselineText = MainText;
        await editor.SaveBaselineCommand.Execute().FirstAsync();

        LoadoutIniState.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().ContainSingle(s => (GamePath)s.Path == MainPath);
        editor.HasBaseline.Should().BeTrue();
    }

    // ---- tweak layers

    [Fact]
    public async Task EveryLayerForTheFileIsListedWithItsModLowestPriorityFirstAndTheLoadoutTweakLast()
    {
        var loadout = await SeedBaselines();
        await SeedTweak(loadout, "High mod", $"High [{PrefsName}].ini", "[Display]\r\niSize W=1920\r\n", priority: 20);
        await SeedTweak(loadout, "Low mod", $"Low [{PrefsName}].ini", "[Display]\r\niSize W=800\r\n", priority: 10);
        await SeedTweak(loadout, "Other file", $"Other [{MainName}].ini", "[General]\r\nx=1\r\n", priority: 30);
        await SeedTweak(loadout, "Game settings", $"Loadout [{PrefsName}].ini", "[Display]\r\niSize H=900\r\n", priority: 1, settingsGroup: true);

        var editor = await Editor(loadout, PrefsPath);

        editor.Layers.Select(l => (l.Name, l.SourceName, l.IsLoadoutTweak)).Should().Equal(
            ("Low", "Low mod", false),
            ("High", "High mod", false),
            ("Loadout", "Game settings", true));
        editor.Layers.Should().OnlyContain(l => l.IsActive && l.IsOn);
        editor.LoadoutTweakText.Should().Be("[Display]\r\niSize H=900\r\n");
    }

    [Fact]
    public async Task ALayerOfADisabledModIsListedButNotActive()
    {
        var loadout = await SeedBaselines();
        await SeedTweak(loadout, "Off mod", $"Off [{PrefsName}].ini", "[Display]\r\niSize W=1\r\n", priority: 10, modDisabled: true);

        var editor = await Editor(loadout, PrefsPath);

        var layer = editor.Layers.Should().ContainSingle().Subject;
        layer.IsOn.Should().BeTrue("the layer's own switch was never touched");
        layer.IsActive.Should().BeFalse("its mod is off");
        editor.ResultText.Should().Contain("iSize W=2560");
    }

    [Fact]
    public async Task SwitchingALayerOffKeepsItOutOfTheResultAndBackOnPutsItIn()
    {
        var loadout = await SeedBaselines();
        await SeedTweak(loadout, "Wide mod", $"Wide [{PrefsName}].ini", "[Display]\r\niSize W=3840\r\n", priority: 10);
        var editor = await Editor(loadout, PrefsPath);
        editor.ResultText.Should().Contain("iSize W=3840");

        await editor.ToggleLayerCommand.Execute(editor.Layers[0]).FirstAsync();

        editor.Layers[0].IsOn.Should().BeFalse();
        editor.Layers[0].IsActive.Should().BeFalse();
        editor.ResultText.Should().Contain("iSize W=2560").And.NotContain("3840");
        (await RenderedText(loadout, PrefsPath)).Should().Be(editor.ResultText, "the switch is the loadout's state, so Apply sees it too");

        await editor.ToggleLayerCommand.Execute(editor.Layers[0]).FirstAsync();

        editor.Layers[0].IsOn.Should().BeTrue();
        editor.ResultText.Should().Contain("iSize W=3840");
    }

    [Fact]
    public async Task SavingTheLoadoutTweakCreatesTheGameSettingsGroupAndFileOnce()
    {
        var loadout = await SeedBaselines();
        var editor = await Editor(loadout, PrefsPath);

        editor.LoadoutTweakText = "[Display]\r\nbFull Screen=1\r\n";
        await editor.SaveLoadoutTweakCommand.Execute().FirstAsync();
        editor.LoadoutTweakText = "[Display]\r\nbFull Screen=1\r\niSize H=1000\r\n";
        await editor.SaveLoadoutTweakCommand.Execute().FirstAsync();

        var tweaks = IniTweakLayers.All(Fresh(loadout), PrefsPath).Where(l => l.IsLoadoutTweak).ToList();
        tweaks.Should().ContainSingle("saving twice updates the file instead of adding another");
        Encoding.UTF8.GetString(await FileStore.Load(tweaks[0].Hash)).Should().Be("[Display]\r\nbFull Screen=1\r\niSize H=1000\r\n");
        editor.ResultText.Should().Contain("bFull Screen=1").And.Contain("iSize H=1000");
        LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup()
            .Count(g => g.IsGameSettingsGroup).Should().Be(1);
    }

    [Fact]
    public async Task TheLoadoutTweakGoesIntoTheExistingGameSettingsGroup()
    {
        var loadout = await SeedBaselines();
        await SeedTweak(loadout, "Existing settings", $"Loadout [{MainName}].ini", "[General]\r\nx=1\r\n", priority: 1, settingsGroup: true);
        var editor = await Editor(loadout, PrefsPath);

        editor.LoadoutTweakText = "[Display]\r\nbFull Screen=1\r\n";
        await editor.SaveLoadoutTweakCommand.Execute().FirstAsync();

        LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup()
            .Count(g => g.IsGameSettingsGroup).Should().Be(1);
        IniTweakLayers.All(Fresh(loadout), PrefsPath).Should().ContainSingle(l => l.IsLoadoutTweak);
    }

    // ---- typed settings

    [Fact]
    public async Task EachSettingIsOfferedOnlyOnTheFileTheGameHonoursItIn()
    {
        var loadout = await SeedBaselines();

        var main = await Editor(loadout, MainPath);
        var prefs = await Editor(loadout, PrefsPath);

        prefs.Settings.Select(s => s.Definition.Key).Should().Contain(["bFull Screen", "iSize W", "iSize H", "iPresentInterval"]);
        prefs.Settings.Select(s => s.Definition.Key).Should().NotContain(["bInvalidateOlderFiles", "sLocalSavePath"]);
        main.Settings.Select(s => s.Definition.Key).Should().Contain(["bInvalidateOlderFiles", "sLocalSavePath", "sTestFile1"]);
        main.Settings.Select(s => s.Definition.Key).Should().NotContain("iSize W");
    }

    [Fact]
    public async Task ASettingShowsTheValueTheResultHas()
    {
        var loadout = await SeedBaselines();
        await SeedTweak(loadout, "Wide mod", $"Wide [{PrefsName}].ini", "[Display]\r\niSize W=3840\r\n", priority: 10);

        var editor = await Editor(loadout, PrefsPath);

        editor.Settings.Single(s => s.Definition.Key == "iSize W").Value.Should().Be("3840");
        editor.Settings.Single(s => s.Definition.Key == "iSize H").Value.Should().Be("1440");
        editor.Settings.Single(s => s.Definition.Key == "iPresentInterval").Value.Should().BeEmpty("no file sets it");
    }

    [Fact]
    public async Task ApplyingASettingWritesItIntoTheLoadoutTweakAndTheResult()
    {
        var loadout = await SeedBaselines();
        var editor = await Editor(loadout, PrefsPath);
        var width = editor.Settings.Single(s => s.Definition.Key == "iSize W");

        width.Value = "1920";
        await width.ApplyCommand.Execute().FirstAsync();

        editor.LoadoutTweakText.Should().Contain("[Display]").And.Contain("iSize W=1920");
        editor.ResultText.Should().Contain("iSize W=1920");
        (await RenderedText(loadout, PrefsPath)).Should().Be(editor.ResultText);
        IniDocument.Parse(await FileStore.Load(LoadoutIniState.FindByLoadout(Connection.Db, loadout.LoadoutId).Single(s => (GamePath)s.Path == PrefsPath).Baseline))
            .Get("Display", "iSize W").Should().Be("2560", "the baseline is the player's copy and a setting never edits it");
    }

    [Fact]
    public async Task AValueOfTheWrongTypeIsRefusedWithADialogAndNothingIsWritten()
    {
        var loadout = await SeedBaselines();
        var editor = await Editor(loadout, PrefsPath);
        var width = editor.Settings.Single(s => s.Definition.Key == "iSize W");

        width.Value = "wide";
        await width.ApplyCommand.Execute().FirstAsync();

        _presenter.Dialogs.Should().ContainSingle();
        PresenterRig.Body(_presenter.Dialogs[0]).Should().Contain("iSize W");
        editor.LoadoutTweakText.Should().BeEmpty();
        IniTweakLayers.All(Fresh(loadout), PrefsPath).Should().BeEmpty();
    }

    [Fact]
    public async Task ASettingTheAppManagesIsShownAsManagedAndCannotBeApplied()
    {
        var loadout = await SeedBaselines();
        var editor = await Editor(loadout, MainPath);

        var archive = editor.Settings.Single(s => s.Definition.Key == "bInvalidateOlderFiles");
        var language = editor.Settings.Single(s => s.Definition.Key == "sTestFile1");

        archive.IsManaged.Should().BeTrue();
        archive.ManagedReason.Should().NotBeNullOrWhiteSpace();
        (await archive.ApplyCommand.CanExecute.FirstAsync()).Should().BeFalse("the bake sets it after the tweaks, so a tweak would do nothing");
        language.IsManaged.Should().BeFalse();
        (await language.ApplyCommand.CanExecute.FirstAsync()).Should().BeTrue();
    }

    // ---- result and diff

    [Fact]
    public async Task TheResultIsWhatTheBakeRenders_AndTheDiffAgainstTheBaselineShowsTheChanges()
    {
        var loadout = await SeedBaselines();
        await SeedTweak(loadout, "Wide mod", $"Wide [{MainName}].ini", "[General]\r\nsLanguage=FRENCH\r\n", priority: 10);

        var editor = await Editor(loadout, MainPath);

        editor.ResultText.Should().Be(await RenderedText(loadout, MainPath));
        editor.ResultText.Should().Contain("sLanguage=FRENCH").And.Contain("bInvalidateOlderFiles=1");
        editor.Diff.Where(d => d.Kind == IniDiffKind.Removed).Select(d => d.Text).Should().Equal("sLanguage=ENGLISH");
        editor.Diff.Where(d => d.Kind == IniDiffKind.Added && d.Text != "").Select(d => d.Text).Should().Equal("sLanguage=FRENCH", "[Archive]", "bInvalidateOlderFiles=1");
    }

    // ---- resets

    [Fact]
    public async Task ResetToBaselineClearsTheLoadoutTweak()
    {
        var loadout = await SeedBaselines();
        var editor = await Editor(loadout, PrefsPath);
        var width = editor.Settings.Single(s => s.Definition.Key == "iSize W");
        width.Value = "1920";
        await width.ApplyCommand.Execute().FirstAsync();

        await editor.ResetToBaselineCommand.Execute().FirstAsync();

        editor.LoadoutTweakText.Should().BeEmpty();
        editor.ResultText.Should().Be(PrefsText);
        (await RenderedText(loadout, PrefsPath)).Should().Be(PrefsText);
    }

    [Fact]
    public async Task ResetToBaselineLeavesModLayersAlone()
    {
        var loadout = await SeedBaselines();
        await SeedTweak(loadout, "Wide mod", $"Wide [{PrefsName}].ini", "[Display]\r\niSize W=3840\r\n", priority: 10);
        var editor = await Editor(loadout, PrefsPath);

        await editor.ResetToBaselineCommand.Execute().FirstAsync();

        editor.Layers.Should().ContainSingle(l => l.Name == "Wide" && l.IsOn);
        editor.ResultText.Should().Contain("iSize W=3840");
    }

    // ---- baseline from disk

    private static AbsolutePath LiveFile(Loadout.ReadOnly loadout, GamePath path) => loadout.InstallationInstance.Locations.ToAbsolutePath(path);

    private async Task<byte[]> BaselineBytes(Loadout.ReadOnly loadout, GamePath path) =>
        await FileStore.Load(LoadoutIniState.FindByLoadout(Connection.Db, loadout.LoadoutId).Single(s => (GamePath)s.Path == path).Baseline);

    [Fact]
    public async Task ResetBaselineFromDiskAsksFirstAndKeepsTheBaselineOnNo()
    {
        var loadout = await SeedBaselines();
        var live = LiveFile(loadout, PrefsPath);
        await live.WriteAllTextAsync("[Display]\r\niSize W=640\r\n");
        try
        {
            _presenter.Answer = _ => Task.FromResult(new StandardDialogResult { ButtonId = ButtonDefinitionId.No });
            var editor = await Editor(loadout, PrefsPath);

            await editor.ResetBaselineFromDiskCommand.Execute().FirstAsync();

            _presenter.Dialogs.Should().ContainSingle("the live file replaces the player's copy, so the user is asked");
            Encoding.UTF8.GetString(await BaselineBytes(loadout, PrefsPath)).Should().Be(PrefsText);
        }
        finally
        {
            live.Delete();
        }
    }

    [Fact]
    public async Task ResetBaselineFromDiskReadsTheLiveFileIntoTheBaselineOnYes()
    {
        var loadout = await SeedBaselines();
        var live = LiveFile(loadout, PrefsPath);
        const string onDisk = "[Display]\r\niSize W=640\r\n";
        await live.WriteAllTextAsync(onDisk);
        try
        {
            _presenter.Answer = _ => Task.FromResult(new StandardDialogResult { ButtonId = ButtonDefinitionId.Accept });
            var editor = await Editor(loadout, PrefsPath);

            await editor.ResetBaselineFromDiskCommand.Execute().FirstAsync();

            Encoding.UTF8.GetString(await BaselineBytes(loadout, PrefsPath)).Should().Be(onDisk);
            editor.BaselineText.Should().Be(onDisk);
            editor.ResultText.Should().Be(onDisk);
        }
        finally
        {
            live.Delete();
        }
    }

    [Fact]
    public async Task ResetBaselineFromDiskWithNoFileOnDiskTellsTheUserAndChangesNothing()
    {
        var loadout = await SeedBaselines();
        LiveFile(loadout, PrefsPath).FileExists.Should().BeFalse();
        _presenter.Answer = _ => Task.FromResult(new StandardDialogResult { ButtonId = ButtonDefinitionId.Accept });
        var editor = await Editor(loadout, PrefsPath);

        await editor.ResetBaselineFromDiskCommand.Execute().FirstAsync();

        Encoding.UTF8.GetString(await BaselineBytes(loadout, PrefsPath)).Should().Be(PrefsText);
        _presenter.Dialogs.Should().NotBeEmpty();
        PresenterRig.Body(_presenter.Dialogs[^1]).Should().Contain(PrefsName);
    }
}
