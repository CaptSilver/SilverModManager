using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FromSoft.Doctor;
using SilverModManager.Games.FromSoft.Saves;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Games.FromSoft.Launch;

/// <summary>
/// Keeps a modded session from changing the vanilla save unnoticed. The overlay does not cover the Wine prefix, so
/// the save folder is the other way a modded session can end up marking a save that is later played online. Does
/// nothing for another game.
/// </summary>
/// <remarks>
/// Preparing a modded launch copies the vanilla save (<see cref="FromSoftSaveNames.Vanilla"/>) and its <c>.bak</c> of
/// every account folder and records them in the <see cref="SaveLedger"/>. Finishing compares: an unchanged file drops
/// its copy; a changed one is kept as <c>&lt;save&gt;.&lt;stamp&gt;-modded-session</c> and the copy is put back, which leaves a record for
/// <see cref="SaveWrittenByModdedSessionEmitter"/>. When the copy cannot be put back the entry is tainted and the
/// copy kept. Finish does not run when the game could not be shut down, so every check first settles the entries a
/// session left behind. An unmodded launch is refused while an entry is tainted, and while the game folder holds mod
/// files next to the anti-cheat launcher.
/// <para>
/// Only the vanilla save and its <c>.bak</c> are touched. The Seamless Co-op save, the modded save (the one a modded
/// profile is pointed at) and any file the module does not know are never read, moved or restored.
/// </para>
/// <para>
/// This is the backstop to the profile's save file rule and the Seamless Co-op save's own extension: the guard protects
/// the vanilla save, and a modded session touches it only when the game's loader cannot redirect saves
/// (<see cref="FromSoftSaveNames.Modded"/> is <c>null</c>), in which case this copy-and-restore is the only
/// protection. It cannot undo a Steam Cloud upload: Steam may upload a changed save after the game exits and before
/// Finish puts the copy back, and the saves page's cloud-purge instructions are for that case.
/// </para>
/// </remarks>
public sealed class VanillaSaveGate(
    IConnection connection,
    ISettingsManager settings,
    IFileSystem fileSystem,
    IPrefixResolver prefixes,
    ILogger<VanillaSaveGate> logger) : ILaunchGate
{
    /// <inheritdoc/>
    public Veto? Check(LaunchContext context)
    {
        if (context.Installation.Game is not IFromSoftGame game) return null;

        var ledger = LedgerOf(context);
        // The interface is synchronous; hashing a save is quick, and the work runs off the caller's thread.
        Task.Run(() => Settle(ledger, game.SaveNames)).GetAwaiter().GetResult();

        // A modded session is what a taint protects against, so it is never refused for one.
        if (IsModded(context, game)) return null;

        if (ledger.AnyTainted)
            return new Veto("The vanilla save was changed by a modded session and could not be restored; use the saves page to resolve it.");

        var artifacts = FromSoftChecks.GameFolder(FromSoftPaths.GameFolder(game.Layout, context.Installation), game.CheckRules).First(r => r.Id == "artifacts");
        return artifacts.Level == CheckLevel.Fail
            ? new Veto($"Cannot start {context.Installation.Game.DisplayName} without mods: {artifacts.Message}")
            : null;
    }

    /// <inheritdoc/>
    public async Task Prepare(LaunchContext context, CancellationToken cancellationToken)
    {
        if (context.Installation.Game is not IFromSoftGame game || !IsModded(context, game)) return;

        var resolution = prefixes.Resolve(context.Installation);
        if (!resolution.IsResolved)
        {
            logger.LogWarning("The saves of {Game} are not guarded during this session: {Reason}", context.Installation.Game.DisplayName, resolution.Diagnostic);
            return;
        }

        var ledger = LedgerOf(context);
        var existing = ledger.Entries.ToDictionary(e => e.Folder);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff'Z'");

        foreach (var folder in SaveFolders(game.Layout, resolution.Prefix.Path))
        {
            // A tainted save is already suspect; copying it now would replace the copy that is still good.
            if (existing.TryGetValue(folder, out var entry) && entry.Tainted) continue;

            var copyPath = (ledger.Session / stamp / Path.GetFileName(folder)).GetFullPath();
            Directory.CreateDirectory(copyPath);
            foreach (var name in SaveFilesOf(game.SaveNames))
            {
                var source = Path.Combine(folder, name);
                if (File.Exists(source)) File.Copy(source, Path.Combine(copyPath, name), overwrite: true);
            }

            var vanilla = Path.Combine(folder, game.SaveNames.Vanilla);
            var hash = File.Exists(vanilla) ? await SaveLedger.HashOf(fileSystem, vanilla, cancellationToken) : "";
            ledger.Put(new SaveLedgerEntry(folder, hash, copyPath, Tainted: false, stamp));
        }
    }

    /// <inheritdoc/>
    public async Task Finish(LaunchContext context)
    {
        if (context.Installation.Game is not IFromSoftGame game) return;
        await Settle(LedgerOf(context), game.SaveNames);
    }

    /// <summary>Compares and restores every entry that is not tainted: a session that has ended, or whose end was never seen.</summary>
    private async Task Settle(SaveLedger ledger, FromSoftSaveNames names)
    {
        foreach (var entry in ledger.Entries.Where(e => !e.Tainted))
        {
            if (await Reconcile(ledger, entry, names)) ledger.Remove(entry);
            else ledger.Put(entry with { Tainted = true });
        }
    }

    /// <summary>Puts the folder's save files back as the entry's copies have them. False when something could not be restored.</summary>
    private async Task<bool> Reconcile(SaveLedger ledger, SaveLedgerEntry entry, FromSoftSaveNames names)
    {
        var restored = true;
        foreach (var name in SaveFilesOf(names))
        {
            var current = Path.Combine(entry.Folder, name);
            var copy = Path.Combine(entry.CopyPath, name);
            try
            {
                if (!await IsChanged(entry, names, name, current, copy)) continue;
                restored &= Restore(ledger, entry, name, current, copy);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogError(exception, "Could not settle {File} after a modded session", current);
                restored = false;
            }
        }

        return restored;
    }

    private async Task<bool> IsChanged(SaveLedgerEntry entry, FromSoftSaveNames names, string name, string current, string copy)
    {
        var existedBefore = File.Exists(copy);
        if (File.Exists(current) != existedBefore) return true;
        if (!existedBefore) return false;

        var before = name == names.Vanilla ? entry.Hash : await SaveLedger.HashOf(fileSystem, copy);
        return await SaveLedger.HashOf(fileSystem, current) != before;
    }

    private bool Restore(SaveLedger ledger, SaveLedgerEntry entry, string name, string current, string copy)
    {
        var backup = "";
        if (File.Exists(current))
        {
            Directory.CreateDirectory(ledger.Backups.GetFullPath());
            backup = UniqueBackup(ledger, name, entry);
            File.Copy(current, backup);
        }

        try
        {
            if (File.Exists(copy))
            {
                // Renamed over the save, never written into it: a failure leaves the file as it was.
                var temporary = current + ".restore";
                File.Copy(copy, temporary, overwrite: true);
                File.Move(temporary, current, overwrite: true);
            }
            else
            {
                File.Delete(current);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "A modded session changed {Save} and it could not be restored; the copy is kept in {Copy}", current, entry.CopyPath);
            ledger.Add(new SaveWrittenRecord(current, backup, Restored: false, entry.Stamp));
            return false;
        }

        logger.LogError("A modded session changed {Save}; its bytes are kept in {Backup} and the save was restored", current, backup);
        ledger.Add(new SaveWrittenRecord(current, backup, Restored: true, entry.Stamp));
        return true;
    }

    private static string UniqueBackup(SaveLedger ledger, string name, SaveLedgerEntry entry)
    {
        var baseName = Path.Combine(ledger.Backups.GetFullPath(), $"{name}.{entry.Stamp}-modded-session");
        var candidate = baseName;
        for (var i = 2; File.Exists(candidate); i++) candidate = $"{baseName}.{i}";
        return candidate;
    }

    /// <summary>The files the guard copies and restores: the vanilla save and the game's own copy of it.</summary>
    private static string[] SaveFilesOf(FromSoftSaveNames names) => [names.Vanilla, names.Vanilla + ".bak"];

    /// <summary>The account folders of the prefix: the folders under the game's save root named as the layout's account folders are.</summary>
    private static IEnumerable<string> SaveFolders(FromSoftGameLayout layout, AbsolutePath prefix)
    {
        var root = FromSoftPaths.SaveRoot(layout, prefix);
        if (!root.DirectoryExists()) return [];

        return root.EnumerateDirectories(recursive: false)
            .Where(d => FromSoftSaves.IsAccountFolder(layout, d.FileName))
            .Select(d => d.GetFullPath())
            .Order(StringComparer.Ordinal);
    }

    private bool IsModded(LaunchContext context, IFromSoftGame game)
        => game.Loader.IsModded(Loadout.Load(connection.Db, context.LoadoutId));

    private SaveLedger LedgerOf(LaunchContext context)
        => SaveLedger.For(settings.Get<FromSoftSettings>(), fileSystem, context.Installation);
}
