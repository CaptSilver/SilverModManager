using System.Reactive;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.App.UI.Pages.Ini;

/// <summary>
/// The editor of one game INI for a loadout: the loadout's own copy of the file, the tweaks layered over it, the
/// typed settings that write into the loadout's tweak, and what the bake renders from all of it. Everything shown is
/// read from the database and the file store; every change is written there and the editor reloads.
/// </summary>
public sealed partial class IniFileEditor : ReactiveObject
{
    private readonly Loadout.ReadOnly _snapshot;
    private readonly IGameIniFiles _game;
    private readonly IConnection _connection;
    private readonly IFileStore _fileStore;
    private readonly IWindowManager _windows;
    private readonly IErrorPresenter _errors;

    private IniEncoding _baselineEncoding = IniEncoding.Utf8;
    private IniEncoding _tweakEncoding = IniEncoding.Utf8;
    private string _savedBaselineText = "";
    private string _savedTweakText = "";

    /// <param name="loadout">The loadout the INI belongs to.</param>
    /// <param name="game">The game, which says where the file is and which keys the app sets in it.</param>
    /// <param name="path">The INI, one of the game's files.</param>
    /// <param name="services">Where the connection, file store, window manager and error presenter come from.</param>
    public IniFileEditor(Loadout.ReadOnly loadout, IGameIniFiles game, GamePath path, IServiceProvider services)
    {
        _snapshot = loadout;
        _game = game;
        Path = path;
        _connection = services.GetRequiredService<IConnection>();
        _fileStore = services.GetRequiredService<IFileStore>();
        _windows = services.GetRequiredService<IWindowManager>();
        _errors = services.GetRequiredService<IErrorPresenter>();

        SaveBaselineCommand = ReactiveCommand.CreateFromTask(() => Guarded(SaveBaseline, "saving the INI"));
        SaveLoadoutTweakCommand = ReactiveCommand.CreateFromTask(() => Guarded(SaveLoadoutTweak, "saving the loadout tweak"));
        ToggleLayerCommand = ReactiveCommand.CreateFromTask<IniLayerEntry>(layer => Guarded(() => ToggleLayer(layer), "switching the tweak"));
        ResetToBaselineCommand = ReactiveCommand.CreateFromTask(() => Guarded(ResetToBaseline, "resetting the loadout tweak"));
        ResetBaselineFromDiskCommand = ReactiveCommand.CreateFromTask(() => Guarded(ResetBaselineFromDisk, "reading the INI from the game folder"));
    }

    /// <summary>The editors of a game's INI files, one per file in the order the game reads them.</summary>
    public static IReadOnlyList<IniFileEditor> ForGame(Loadout.ReadOnly loadout, IGameIniFiles game, IServiceProvider services) =>
        game.IniFiles.FileNames.Select(name => new IniFileEditor(loadout, game, game.IniPath(name), services)).ToList();

    public GamePath Path { get; }

    public string FileName => Path.FileName.ToString();

    /// <summary>Whether the loadout has its own copy of the file yet. It takes one from the game folder on the first Apply.</summary>
    [Reactive] public partial bool HasBaseline { get; private set; }

    /// <summary>The loadout's copy of the file, editable; <see cref="SaveBaselineCommand"/> stores it.</summary>
    [Reactive] public partial string BaselineText { get; set; } = "";

    /// <summary>The tweak files for this INI in merge order, the loadout's own last.</summary>
    [Reactive] public partial IReadOnlyList<IniLayerEntry> Layers { get; private set; } = [];

    /// <summary>The loadout's own tweak, editable; <see cref="SaveLoadoutTweakCommand"/> stores it.</summary>
    [Reactive] public partial string LoadoutTweakText { get; set; } = "";

    [Reactive] public partial IReadOnlyList<IniSettingEntry> Settings { get; private set; } = [];

    /// <summary>What Apply writes to the game folder for this INI; empty when there is no copy to bake from.</summary>
    [Reactive] public partial string ResultText { get; private set; } = "";

    /// <summary>The copy compared with <see cref="ResultText"/>.</summary>
    [Reactive] public partial IReadOnlyList<IniDiffLine> Diff { get; private set; } = [];

    public ReactiveCommand<Unit, Unit> SaveBaselineCommand { get; }

    public ReactiveCommand<Unit, Unit> SaveLoadoutTweakCommand { get; }

    /// <summary>Switches one tweak file on or off.</summary>
    public ReactiveCommand<IniLayerEntry, Unit> ToggleLayerCommand { get; }

    /// <summary>Clears the loadout's tweak, so the result is the copy plus the mods' tweaks.</summary>
    public ReactiveCommand<Unit, Unit> ResetToBaselineCommand { get; }

    /// <summary>Asks, then replaces the loadout's copy with the file in the game folder.</summary>
    public ReactiveCommand<Unit, Unit> ResetBaselineFromDiskCommand { get; }

    private Loadout.ReadOnly Fresh()
    {
        var loaded = Loadout.Load(_connection.Db, _snapshot.LoadoutId);
        return loaded.IsValid() ? loaded : _snapshot;
    }

    /// <summary>
    /// Reads everything shown from the database and the file store. Text the user has edited and not saved is kept.
    /// </summary>
    public async Task Load()
    {
        var loadout = Fresh();

        var baselineBytes = await TryLoadBaseline(loadout);
        HasBaseline = baselineBytes is not null;
        var baseline = baselineBytes is null ? null : IniDocument.Parse(baselineBytes);
        _baselineEncoding = baseline?.Encoding ?? IniEncoding.Utf8;
        var baselineText = baseline?.ToText() ?? "";
        if (BaselineText == _savedBaselineText) BaselineText = baselineText;
        _savedBaselineText = baselineText;

        var sources = IniTweakLayers.All(loadout, Path);
        var layers = new List<IniLayerEntry>();
        var tweakText = "";
        foreach (var source in sources)
        {
            var document = await TryLoadDocument(source.Hash);
            var text = document?.ToText() ?? "";
            layers.Add(new IniLayerEntry(source.Name, source.SourceName, source.FileId, source.IsLoadoutTweak, source.IsSwitchedOn, source.IsEnabled, text));
            if (!source.IsLoadoutTweak) continue;
            tweakText = text;
            _tweakEncoding = document?.Encoding ?? IniEncoding.Utf8;
        }
        Layers = layers;
        if (LoadoutTweakText == _savedTweakText) LoadoutTweakText = tweakText;
        _savedTweakText = tweakText;

        var resultText = "";
        if (baselineBytes is not null)
            resultText = IniDocument.Parse(await GameInis.Render(_game, _fileStore, loadout, Path)).ToText();
        ResultText = resultText;
        Diff = IniDiff.Lines(baselineText, resultText);
        Settings = BuildSettings(loadout, resultText);
    }

    private async Task<byte[]?> TryLoadBaseline(Loadout.ReadOnly loadout)
    {
        try
        {
            return await GameInis.LoadBaseline(_fileStore, loadout, Path);
        }
        catch (IniBaselineMissingException)
        {
            return null;
        }
    }

    private async Task<IniDocument?> TryLoadDocument(Hash hash) =>
        await _fileStore.HaveFile(hash) ? IniDocument.Parse(await _fileStore.Load(hash)) : null;

    private IReadOnlyList<IniSettingEntry> BuildSettings(Loadout.ReadOnly loadout, string resultText)
    {
        var result = IniDocument.Parse(resultText);
        var managed = _game.ManagedKeysFile == Path ? _game.ManagedKeys(loadout) : [];
        return IniSettingsCatalog.All
            .Where(definition => IniSettingsCatalog.FileFor(_game.IniFiles, definition.File) == FileName)
            .Select(definition =>
            {
                var isManaged = managed.Any(k => k.Section.Equals(definition.Section, StringComparison.OrdinalIgnoreCase) && k.Key.Equals(definition.Key, StringComparison.OrdinalIgnoreCase));
                return new IniSettingEntry(definition, result.Get(definition.Section, definition.Key) ?? "", isManaged ? Language.IniPage_ManagedReason : null, entry => Guarded(() => ApplySetting(entry), "saving the setting"));
            })
            .ToList();
    }

    private async Task SaveBaseline()
    {
        var bytes = IniDocument.Parse(BaselineText, _baselineEncoding).ToBytes();
        await IniEdits.SetBaseline(_connection, _fileStore, Fresh(), Path, bytes);
        await Load();
    }

    private async Task SaveLoadoutTweak()
    {
        await IniEdits.SetLoadoutTweak(_connection, _fileStore, Fresh(), Path, IniDocument.Parse(LoadoutTweakText, _tweakEncoding).ToBytes());
        await Load();
    }

    private async Task ToggleLayer(IniLayerEntry layer)
    {
        await IniEdits.SetTweakSwitch(_connection, layer.File, !layer.IsOn);
        await Load();
    }

    private async Task ApplySetting(IniSettingEntry setting)
    {
        var definition = setting.Definition;
        if (!definition.Accepts(setting.Value))
            throw new IniEditException(Language.IniPage_InvalidValue_Title, string.Format(Language.IniPage_InvalidValue, definition.Key, definition.Section, definition.TypeDescription, setting.Value));

        // Edit the saved tweak, not the text box: unsaved typing there is not part of this change.
        var tweak = IniDocument.Parse(_savedTweakText, _tweakEncoding);
        tweak.Set(definition.Section, definition.Key, setting.Value);
        await IniEdits.SetLoadoutTweak(_connection, _fileStore, Fresh(), Path, tweak.ToBytes());
        await Load();
    }

    private async Task ResetToBaseline()
    {
        await IniEdits.SetLoadoutTweak(_connection, _fileStore, Fresh(), Path, []);
        await Load();
    }

    private async Task ResetBaselineFromDisk()
    {
        var loadout = Fresh();
        var locations = loadout.InstallationInstance.Locations;
        var file = locations.ContainsKey(Path.LocationId) ? locations.ToAbsolutePath(Path) : default;
        if (file == default || !file.FileExists)
            throw new IniEditException(Language.IniPage_NoFileOnDisk_Title, string.Format(Language.IniPage_NoFileOnDisk, FileName));

        var yes = DialogStandardButtons.Yes with { ButtonStyling = ButtonStyling.Default };
        var no = DialogStandardButtons.No with { ButtonStyling = ButtonStyling.Primary };
        var dialog = DialogFactory.CreateStandardDialog(
            Language.IniPage_ResetFromDisk_Title,
            new StandardDialogParameters { Text = string.Format(Language.IniPage_ResetFromDisk_Text, FileName) },
            [no, yes],
            DialogWindowSize.Medium);
        var answer = await _windows.ShowDialog(dialog, DialogWindowType.Modal);
        if (answer.ButtonId != ButtonDefinitionId.Accept) return;

        await IniEdits.SetBaseline(_connection, _fileStore, loadout, Path, await file.ReadAllBytesAsync());
        BaselineText = _savedBaselineText;
        await Load();
    }

    private async Task Guarded(Func<Task> action, string activity)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            await _errors.Report(exception, activity);
        }
    }
}
