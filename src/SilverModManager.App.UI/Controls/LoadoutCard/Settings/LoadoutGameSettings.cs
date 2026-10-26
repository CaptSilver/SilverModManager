using System.Reactive;
using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.Games.FromSoft;
using SilverModManager.Games.FromSoft.Saves;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using FromSoftSavesService = SilverModManager.Games.FromSoft.Saves.FromSoftSaves;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.App.UI.Controls.LoadoutCard;

/// <summary>
/// The two per-loadout choices about the game's own files, saves and INIs, and what follows from them: offering to
/// bring the current save along when saves are kept apart, and offering to delete a loadout's saves with it.
/// </summary>
/// <remarks>
/// Turning "keep saves" on switches the game to an empty slot, so the user is first offered a copy of the save they
/// play today. The copy only ever creates files: a destination that exists is left alone and the change is refused.
/// </remarks>
public sealed partial class LoadoutGameSettings : ReactiveObject
{
    private const string BackupSuffix = ".bak";

    private readonly Loadout.ReadOnly _snapshot;
    private readonly IConnection _connection;
    private readonly IWindowManager _windows;
    private readonly IFileSystem _fileSystem;
    private readonly IErrorPresenter _errors;
    private readonly ILogger<LoadoutGameSettings> _logger;
    private readonly IPerLoadoutSaves? _saves;
    private readonly Func<IReadOnlyList<AbsolutePath>> _saveRoots;

    /// <param name="loadout">The loadout the choices belong to.</param>
    /// <param name="services">Where the window manager, file system, error presenter and logger come from.</param>
    /// <param name="saves">The game's per-loadout saves, or <c>null</c> when it keeps no save slots.</param>
    /// <param name="iniFiles">The INI files the game reads, or <c>null</c> when it has none.</param>
    /// <param name="saveRoots">
    /// The folders <paramref name="saves"/> plans against: one per place the game keeps saves, such as each Steam
    /// account's folder. Read each time it is needed, since the folders can appear after the page opens.
    /// </param>
    public LoadoutGameSettings(Loadout.ReadOnly loadout, IServiceProvider services, IPerLoadoutSaves? saves, IniSet? iniFiles, Func<IReadOnlyList<AbsolutePath>> saveRoots)
    {
        _snapshot = loadout;
        _connection = services.GetRequiredService<IConnection>();
        _windows = services.GetRequiredService<IWindowManager>();
        _fileSystem = services.GetRequiredService<IFileSystem>();
        _errors = services.GetRequiredService<IErrorPresenter>();
        _logger = services.GetRequiredService<ILogger<LoadoutGameSettings>>();
        _saves = saves;
        _saveRoots = saveRoots;

        KeepSavesWithLoadout = Loadout.KeepSavesWithLoadout.Get(loadout);
        OwnsGameInis = Loadout.OwnsGameInis.Get(loadout);
        ShowOwnsGameInis = iniFiles is not null;
        (CanKeepSaves, KeepSavesToolTip) = DescribeKeepSaves(loadout);

        // Switching off stays possible when the game has since stopped supporting it, or the flag could never be cleared.
        ToggleKeepSavesCommand = ReactiveCommand.CreateFromTask(
            ToggleKeepSaves,
            this.WhenAnyValue(vm => vm.KeepSavesWithLoadout, vm => vm.CanKeepSaves, (keep, can) => keep || can));
        ToggleOwnsGameInisCommand = ReactiveCommand.CreateFromTask(ToggleOwnsGameInis);
    }

    /// <summary>
    /// Builds the settings of a loadout from its game: the save slots and INI files it declares, and the folders
    /// the saves live in.
    /// </summary>
    public static LoadoutGameSettings For(Loadout.ReadOnly loadout, IServiceProvider services)
    {
        loadout.TryGetGame(out var game);
        return new LoadoutGameSettings(
            loadout,
            services,
            game as IPerLoadoutSaves,
            (game as IHasIniFiles)?.IniFiles,
            () => SaveRootsOf(loadout.InstallationInstance, services)
        );
    }

    // FromSoft keeps one folder per Steam account in the prefix. Other games plan against the folder their
    // preferences live in, which is where a save path set in their INI is relative to.
    private static IReadOnlyList<AbsolutePath> SaveRootsOf(GameInstallation installation, IServiceProvider services)
    {
        if (installation.Game is IFromSoftGame)
        {
            var fileSystem = services.GetRequiredService<IFileSystem>();
            return services.GetRequiredService<FromSoftSavesService>().List(installation).Folders
                .Select(folder => fileSystem.FromUnsanitizedFullPath(folder.Folder))
                .ToArray();
        }

        return installation.Locations.TryGetValue(LocationId.Preferences, out var preferences) ? [preferences.Path] : [];
    }

    [Reactive] public partial bool KeepSavesWithLoadout { get; private set; }

    [Reactive] public partial bool OwnsGameInis { get; private set; }

    /// <summary>Whether the user can turn "keep saves" on for this game and loadout.</summary>
    [Reactive] public partial bool CanKeepSaves { get; private set; }

    /// <summary>Why "keep saves" is disabled, or what to know before turning it on.</summary>
    [Reactive] public partial string KeepSavesToolTip { get; private set; }

    /// <summary>Whether to show "own the game INIs" at all: only a game that reads INI files has anything to own.</summary>
    public bool ShowOwnsGameInis { get; }

    public ReactiveCommand<Unit, Unit> ToggleKeepSavesCommand { get; }

    public ReactiveCommand<Unit, Unit> ToggleOwnsGameInisCommand { get; }

    private (bool Can, string ToolTip) DescribeKeepSaves(Loadout.ReadOnly loadout)
    {
        var plan = _saves?.PlanSaveRedirect(loadout);
        return plan switch
        {
            null => (false, Language.LoadoutGameSettings_KeepSaves_NotSupported),
            SaveRedirectPlan.Unsupported unsupported => (false, unsupported.Reason),
            // A save path set in an INI puts the saves in a subfolder, which Steam Cloud may not pick up.
            SaveRedirectPlan.IniKey => (true, $"{Language.LoadoutGameSettings_KeepSaves_ToolTip} {Language.LoadoutGameSettings_KeepSaves_CloudCaveat}"),
            _ => (true, Language.LoadoutGameSettings_KeepSaves_ToolTip),
        };
    }

    private Loadout.ReadOnly Fresh()
    {
        var loaded = Loadout.Load(_connection.Db, _snapshot.LoadoutId);
        return loaded.IsValid() ? loaded : _snapshot;
    }

    private async Task Write(BooleanAttribute attribute, bool value)
    {
        using var tx = _connection.BeginTransaction();
        tx.Add(_snapshot.Id, attribute, value);
        await tx.Commit();
    }

    private async Task ToggleOwnsGameInis()
    {
        var value = !OwnsGameInis;
        if (!value && KeepSavesWithLoadout)
        {
            // The save folder is set through the loadout's INI, so without it the game falls back to its normal one.
            var answer = await _windows.ShowDialog(
                YesNoDialog(Language.LoadoutGameSettings_DisownInis_Title, Language.LoadoutGameSettings_DisownInis_Text, defaultYes: false),
                DialogWindowType.Modal);
            if (answer.ButtonId != ButtonDefinitionId.Accept) return;

            await Write(Loadout.KeepSavesWithLoadout, false);
            KeepSavesWithLoadout = false;
        }

        await Write(Loadout.OwnsGameInis, value);
        OwnsGameInis = value;
        (CanKeepSaves, KeepSavesToolTip) = DescribeKeepSaves(Fresh());
    }

    private async Task ToggleKeepSaves()
    {
        if (KeepSavesWithLoadout)
        {
            await Write(Loadout.KeepSavesWithLoadout, false);
            KeepSavesWithLoadout = false;
            return;
        }

        var plans = PlansToAdopt(Fresh());
        if (plans.Count > 0)
        {
            var answer = await _windows.ShowDialog(AdoptDialog(plans), DialogWindowType.Modal);
            if (answer.ButtonId == ButtonDefinitionId.Accept)
            {
                if (!await TryCopy(plans)) return;
            }
            else if (answer.ButtonId != ButtonDefinitionId.No)
            {
                // Closing the dialog is neither yes nor no, so the choice is not made.
                return;
            }
        }

        await Write(Loadout.KeepSavesWithLoadout, true);
        KeepSavesWithLoadout = true;
    }

    private List<SaveAdoptionPlan> PlansToAdopt(Loadout.ReadOnly loadout)
        => _saves is null
            ? []
            : _saveRoots().Select(root => _saves.PlanAdoptCurrentSave(loadout, root)).OfType<SaveAdoptionPlan>().Where(plan => plan.Copies.Count > 0).ToList();

    private static IDialog AdoptDialog(List<SaveAdoptionPlan> plans)
    {
        var lines = plans.SelectMany(plan => plan.Copies.Select(copy => string.Format(Language.LoadoutGameSettings_AdoptSave_Copy, copy.Source, copy.Destination)));
        return YesNoDialog(
            Language.LoadoutGameSettings_AdoptSave_Title,
            string.Join("\n", plans.Select(plan => plan.Description).Concat(lines)),
            defaultYes: true);
    }

    private static IDialog YesNoDialog(string title, string text, bool defaultYes)
    {
        var yes = DialogStandardButtons.Yes with { ButtonStyling = defaultYes ? ButtonStyling.Primary : ButtonStyling.Default };
        var no = DialogStandardButtons.No with { ButtonStyling = defaultYes ? ButtonStyling.Default : ButtonStyling.Primary };
        return DialogFactory.CreateStandardDialog(title, new StandardDialogParameters { Text = text }, [no, yes], DialogWindowSize.Medium);
    }

    /// <summary>
    /// Copies every planned file, or none: the destinations are checked first, and a copy that fails part-way
    /// removes what this run created. Reports a refusal to the user and returns false.
    /// </summary>
    private async Task<bool> TryCopy(List<SaveAdoptionPlan> plans)
    {
        var created = new List<AbsolutePath>();
        SaveAdoptionPlan.Copy? running = null;
        try
        {
            var copies = plans.SelectMany(plan => plan.Copies).ToList();
            foreach (var copy in copies)
            {
                if (_fileSystem.FileExists(copy.Destination))
                    throw new LoadoutSaveFilesException(Language.LoadoutGameSettings_CopyRefusedTitle, string.Format(Language.LoadoutGameSettings_AlreadyThere, copy.Destination));
            }

            foreach (var copy in copies)
            {
                running = copy;
                _fileSystem.CreateDirectory(copy.Destination.Parent);
                await using var input = _fileSystem.ReadFile(copy.Source);
                // CreateNew fails rather than replace a file that appeared since the check above.
                await using var output = _fileSystem.OpenFile(copy.Destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                created.Add(copy.Destination);
                await input.CopyToAsync(output);
            }

            return true;
        }
        catch (Exception exception) when (exception is LoadoutSaveFilesException or IOException or UnauthorizedAccessException)
        {
            foreach (var path in created) TryDelete(path);
            var refusal = exception as LoadoutSaveFilesException
                          ?? new LoadoutSaveFilesException(Language.LoadoutGameSettings_CopyRefusedTitle, string.Format(Language.LoadoutGameSettings_CopyFailed, running?.Source, exception.Message), exception);
            await _errors.Report(refusal, "copying the save into the loadout");
            return false;
        }
    }

    private void TryDelete(AbsolutePath path)
    {
        try
        {
            _fileSystem.DeleteFile(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not remove {Path} after a failed copy", path);
        }
    }

    /// <summary>
    /// The files and folders that hold this loadout's saves right now: empty unless the loadout keeps its saves
    /// apart. Only paths named for the loadout's slot id inside a save root are returned, whatever the game's plan
    /// says, so a plan can never point a delete at the user's other saves.
    /// </summary>
    public IReadOnlyList<AbsolutePath> FindSaveLocations()
    {
        var loadout = Fresh();
        if (_saves is null || !Loadout.KeepSavesWithLoadout.Get(loadout)) return [];

        var slot = LoadoutSaveSlot.IdOf(loadout);
        var plan = _saves.PlanSaveRedirect(loadout);
        var found = new List<AbsolutePath>();
        foreach (var root in _saveRoots())
        {
            foreach (var candidate in CandidatesFor(plan, root, slot))
            {
                if (_fileSystem.FileExists(candidate) || _fileSystem.DirectoryExists(candidate)) found.Add(candidate);
            }
        }

        return found;
    }

    private static IEnumerable<AbsolutePath> CandidatesFor(SaveRedirectPlan? plan, AbsolutePath root, string slot)
    {
        switch (plan)
        {
            // The loader is handed a file name in the save folder; its backup sits beside it.
            case SaveRedirectPlan.LoaderKey { Value: var name } when name.Contains(slot, StringComparison.Ordinal) && name.IndexOfAny(['/', '\\']) < 0:
                yield return root / name;
                yield return root / (name + BackupSuffix);
                break;
            // The INI holds a folder relative to the save root, such as Saves\<slot>\.
            case SaveRedirectPlan.IniKey { Value: var value }:
                var segments = value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
                var rooted = value.StartsWith('/') || value.StartsWith('\\');
                if (rooted || segments.Length == 0 || segments.Any(s => s is "." or "..") || segments[^1] != slot) yield break;

                var path = root;
                foreach (var segment in segments) path /= segment;
                yield return path;
                break;
        }
    }

    /// <summary>
    /// Asks whether to delete <paramref name="locations"/>, the saves of a loadout that has just been deleted, and
    /// deletes them on yes. The answer defaults to no, since the saves are the player's progress.
    /// </summary>
    public async Task OfferToDeleteSaves(IReadOnlyList<AbsolutePath> locations)
    {
        if (locations.Count == 0) return;

        var text = string.Format(Language.LoadoutGameSettings_DeleteSaves_Text, string.Join(", ", locations));
        var answer = await _windows.ShowDialog(YesNoDialog(Language.LoadoutGameSettings_DeleteSaves_Title, text, defaultYes: false), DialogWindowType.Modal);
        if (answer.ButtonId != ButtonDefinitionId.Accept) return;

        foreach (var location in locations)
        {
            try
            {
                if (_fileSystem.DirectoryExists(location)) _fileSystem.DeleteDirectory(location, recursive: true);
                else _fileSystem.DeleteFile(location);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                var refusal = new LoadoutSaveFilesException(Language.LoadoutGameSettings_DeleteRefusedTitle, string.Format(Language.LoadoutGameSettings_DeleteFailed, location, exception.Message), exception);
                await _errors.Report(refusal, "deleting the saves of a loadout");
                return;
            }
        }
    }
}
