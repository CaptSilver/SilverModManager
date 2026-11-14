using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Steam.Shortcuts;

/// <summary>
/// Picks the account, builds the entry, runs the route and keeps the record of what it wrote.
/// </summary>
/// <param name="steamRoot">The Steam folder, or null when there is none. A Flatpak one is refused here.</param>
/// <param name="runningExecutable">What the app was started as, so an entry that starts it directly still counts as ours.</param>
/// <param name="registryVdf">Steam's <c>registry.vdf</c>, which names the account it signs in as.</param>
public sealed class SteamShortcutService(
    IProcessTracker tracker,
    IGameModeDetector gameMode,
    IEnumerable<ISteamShortcutRoute> routes,
    ShortcutsVdfWriter writer,
    LauncherShim shim,
    IFileSystem fileSystem,
    IEnvironmentReader environment,
    Func<AbsolutePath?> steamRoot,
    Func<AbsolutePath> runningExecutable,
    AbsolutePath registryVdf) : ISteamShortcutService
{
    private const string FlatpakSteamFolder = "/.var/app/com.valvesoftware.Steam/";

    private AbsolutePath DataHome => LauncherShim.ResolveDataHome(environment, fileSystem);
    private AbsolutePath RecordPath => SteamShortcutRecord.PathIn(DataHome);

    public SteamShortcutStatus Status(uint? accountId = null)
    {
        if (NativeSteam() is not { } root) return new SteamShortcutStatus(SteamShortcutState.NoSteam, null, null, []);

        var choice = SteamAccounts.Pick(root, registryVdf, accountId);
        if (choice is SteamAccountChoice.Ambiguous ambiguous) return new SteamShortcutStatus(SteamShortcutState.AccountUndecided, null, null, ambiguous.Candidates);
        if (choice is not SteamAccountChoice.Chosen { Account: var account }) return new SteamShortcutStatus(SteamShortcutState.NoSteam, null, null, []);

        var record = SteamShortcutRecord.Load(RecordPath);
        var found = ShortcutsVdfWriter.Find(ReadEntries(root, account, removing: false), MatchFor(record, account));
        if (found is not null) return new SteamShortcutStatus(SteamShortcutState.InSteam, account, found.AppId, []);

        // Only an add through Steam's URL route leaves an entry that has not been seen yet; it has no id until it is.
        var pending = record is { Confirmed: false, AppId: null } && record.AccountId == account.AccountId;
        return new SteamShortcutStatus(pending ? SteamShortcutState.AddedUnconfirmed : SteamShortcutState.NotInSteam, account, null, []);
    }

    public async Task AddAsync(string route, uint? accountId, CancellationToken token)
    {
        var chosen = routes.FirstOrDefault(r => r.Name == route)
                     ?? throw Refused($"There is no route called \"{route}\". Use \"file\" with Steam closed or \"steam-url\" with it running.", false);

        var (root, account) = PickAccount(accountId, removing: false);
        var steamRunning = tracker.IsSteamRunning();
        if (route == "file" && steamRunning)
        {
            throw Refused("Steam is running. It rewrites its shortcuts when it exits, so an entry written now would be lost. Close Steam completely (including the tray icon) and try again.", false, steamRunning: true);
        }
        if (route == "steam-url" && !steamRunning)
        {
            throw Refused("Steam is not running. Start Steam first, or add the shortcut with Steam closed.", false);
        }

        EnsureShim();
        var shimPath = shim.ShimPath;
        var icon = route == "file" ? SteamGridArt.WriteIcon(DataHome.Combine("silvermod").Combine("icons")) : DataHome.Combine("silvermod").Combine("icons").Combine("silvermodmanager.png");
        var entry = SteamShortcutEntry.ForLauncher(shimPath.ToString(), icon.ToString());

        // The record goes first so an add that dies halfway leaves something Remove can find. Files an earlier add wrote stay listed.
        var previous = SteamShortcutRecord.Load(RecordPath);
        IReadOnlyList<string> carried = previous is not null && previous.AccountId == account.AccountId ? previous.Written : [];
        new SteamShortcutRecord(account.AccountId, route == "file" ? entry.AppId : previous?.AppId, entry.Exe, false, carried).Save(RecordPath);

        var write = await chosen.AddAsync(new SteamShortcutRequest(account, root, shimPath, icon, MatchFor(previous, account)), token);

        new SteamShortcutRecord(account.AccountId, write.AppId ?? (route == "file" ? entry.AppId : null), entry.Exe, write.Confirmed,
            carried.Concat(write.FilesWritten.Select(f => f.ToString())).Distinct().ToList()).Save(RecordPath);

        if (write.Warning is { } warning) throw Refused(warning, false);
    }

    public Task RemoveAsync(uint? accountId, CancellationToken token)
    {
        if (gameMode.IsGameMode) throw Refused("Remove the shortcut from Desktop Mode. Steam is running this app, so it cannot be edited from here.", true);

        var (root, account) = PickAccount(accountId, removing: true);
        if (tracker.IsSteamRunning())
        {
            throw Refused("Steam is running. It rewrites its shortcuts when it exits, so a removal now would be lost. Close Steam completely (including the tray icon) and try again.", true, steamRunning: true);
        }

        var record = SteamShortcutRecord.Load(RecordPath);
        var match = MatchFor(record, account);
        var inFile = ShortcutsVdfWriter.Find(ReadEntries(root, account, removing: true), match) is not null;
        if (!inFile && record is null) throw Refused("The app is not in Steam's library, so there is nothing to remove.", true);

        if (inFile)
        {
            var edit = writer.Remove(SteamAccounts.ConfigDirectory(root, account.AccountId).Combine("shortcuts.vdf"), match);
            if (edit.Refusal is { } refusal) throw Refused(refusal, true);
            if (edit.Note is { } note) throw Refused(note, true);
        }

        DeleteRecordedFiles(root, record);
        SteamShortcutRecord.Delete(RecordPath);
        return Task.CompletedTask;
    }

    private AbsolutePath? NativeSteam()
    {
        if (steamRoot() is not { } root) return null;
        return root.ToString().Contains(FlatpakSteamFolder, StringComparison.Ordinal) ? null : root;
    }

    private (AbsolutePath Root, SteamAccount Account) PickAccount(uint? accountId, bool removing)
    {
        var root = steamRoot() ?? throw Refused("No native Steam install was found.", removing);
        if (root.ToString().Contains(FlatpakSteamFolder, StringComparison.Ordinal))
        {
            throw Refused("Steam is installed as a Flatpak, which cannot reach the launcher script under ~/.local/share.", removing);
        }

        return SteamAccounts.Pick(root, registryVdf, accountId) switch
        {
            SteamAccountChoice.Chosen chosen => (root, chosen.Account),
            SteamAccountChoice.Ambiguous ambiguous => throw Refused($"More than one Steam account could own the shortcut: {string.Join(", ", ambiguous.Candidates.Select(c => c.PersonaName))}. Pick one.", removing),
            SteamAccountChoice.None none => throw Refused(none.Reason, removing),
            _ => throw new InvalidOperationException("Unhandled account choice."),
        };
    }

    private IReadOnlyList<SteamShortcutEntry> ReadEntries(AbsolutePath root, SteamAccount account, bool removing)
    {
        try { return writer.Read(SteamAccounts.ConfigDirectory(root, account.AccountId).Combine("shortcuts.vdf")); }
        catch (InvalidDataException) { throw Refused(ShortcutsVdfWriter.UnparseableRefusal, removing); }
    }

    private ShortcutMatch MatchFor(SteamShortcutRecord? record, SteamAccount account)
    {
        var exes = new HashSet<string> { SteamShortcutEntry.Quote(shim.ShimPath.ToString()), SteamShortcutEntry.Quote(runningExecutable().ToString()) };
        return new ShortcutMatch(record is not null && record.AccountId == account.AccountId ? record.AppId : null, exes);
    }

    /// <summary>
    /// The shim normally exists because the app refreshes it at startup; a command-line run may be the first.
    /// </summary>
    private void EnsureShim()
    {
        if (shim.ShimPath.FileExists) return;

        var target = runningExecutable();
        if (LauncherShimRefresher.IsDevBuildPath(target.ToString()))
        {
            throw Refused($"The launcher script does not exist yet and {target} is a build output folder, which Steam must not start. Run the installed app once and try again.", false);
        }
        shim.Write(target);
    }

    /// <summary>
    /// Deletes the recorded images, but only inside Steam's per-user folders, so an edited record cannot point the
    /// removal at anything else.
    /// </summary>
    private static void DeleteRecordedFiles(AbsolutePath root, SteamShortcutRecord? record)
    {
        var userdata = root.Combine("userdata").ToString() + "/";
        foreach (var file in record?.Written ?? [])
        {
            var full = Path.GetFullPath(file);
            if (full.StartsWith(userdata, StringComparison.Ordinal) && File.Exists(full)) File.Delete(full);
        }
    }

    private static SteamShortcutRefusedException Refused(string message, bool removing, bool steamRunning = false) => new(message, removing) { SteamRunning = steamRunning };
}
