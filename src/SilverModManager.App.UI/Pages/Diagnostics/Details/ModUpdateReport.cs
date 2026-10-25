using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.ModUpdates;
using SilverModManager.Networking.NexusWebApi;

namespace SilverModManager.App.UI.Pages.Diagnostics;

/// <summary>What the library knows about newer files for one mod.</summary>
public enum ModUpdateOutcome
{
    /// <summary>The file the mod was installed from has a newer one on its Nexus page.</summary>
    NewerFiles,

    /// <summary>The mod has a Nexus page and nothing newer is known for its file.</summary>
    UpToDate,

    /// <summary>The mod was not installed from Nexus, so there is no page to ask about.</summary>
    NoNexusPage,
}

/// <summary>Works out and words the answer to "do these mods have newer files on Nexus?".</summary>
public static class ModUpdateReport
{
    /// <summary>
    /// Reads the update cache for the file <paramref name="mod"/> was installed from. A page can have updates for
    /// some of the library's files and not for this one, so the page alone does not decide it.
    /// </summary>
    public static ModUpdateOutcome Outcome(IDb db, IModUpdateService updates, LoadoutItemId mod)
    {
        if (!LibraryLinkedLoadoutItem.TryGet(db, mod.Value, out var linked)) return ModUpdateOutcome.NoNexusPage;
        if (!NexusModsLibraryItem.TryGet(db, linked.Value.LibraryItemId.Value, out var nexus)) return ModUpdateOutcome.NoNexusPage;

        var onPage = updates.HasModPageUpdatesAvailable(nexus.Value.ModPageMetadata);
        if (!onPage.HasValue) return ModUpdateOutcome.UpToDate;

        var installed = nexus.Value.FileMetadataId;
        return onPage.Value.FileMappings.Any(m => m.File.Id == installed.Value && m.NewerFiles.Length > 0)
            ? ModUpdateOutcome.NewerFiles
            : ModUpdateOutcome.UpToDate;
    }

    /// <param name="mods">Each mod's name and what the cache says about it.</param>
    /// <param name="check">How the fresh check of the cache went; anything but Ok means the answer is only what the app already had.</param>
    /// <param name="checkFailure">Why asking Nexus threw, when it did.</param>
    public static string Describe(IReadOnlyList<(string Name, ModUpdateOutcome Outcome)> mods, CacheUpdaterResultStatus check, string? checkFailure = null)
    {
        string Names(ModUpdateOutcome outcome) => string.Join(", ", mods.Where(m => m.Outcome == outcome).Select(m => m.Name));
        bool Any(ModUpdateOutcome outcome) => mods.Any(m => m.Outcome == outcome);

        var clauses = new List<string>();
        if (Any(ModUpdateOutcome.NewerFiles))
        {
            clauses.Add($"Newer files exist for {Names(ModUpdateOutcome.NewerFiles)}");
            if (Any(ModUpdateOutcome.UpToDate)) clauses.Add($"none for {Names(ModUpdateOutcome.UpToDate)}");
            if (Any(ModUpdateOutcome.NoNexusPage)) clauses.Add($"no Nexus page known for {Names(ModUpdateOutcome.NoNexusPage)}");
        }
        else if (Any(ModUpdateOutcome.UpToDate))
        {
            clauses.Add(Any(ModUpdateOutcome.NoNexusPage) ? "None of the mods on Nexus has a newer file" : "None of these mods has a newer file");
            if (Any(ModUpdateOutcome.NoNexusPage)) clauses.Add($"no Nexus page known for {Names(ModUpdateOutcome.NoNexusPage)}");
        }
        else
        {
            clauses.Add($"No Nexus page known for {Names(ModUpdateOutcome.NoNexusPage)}");
        }

        var lines = new List<string>();
        if (checkFailure is not null) lines.Add($"Nexus could not be asked ({checkFailure}), so this is only what the app already knew.");
        else if (check == CacheUpdaterResultStatus.NotLoggedIn) lines.Add("Log in to Nexus Mods to check for new files. This is only what the app already knew.");
        else if (check != CacheUpdaterResultStatus.Ok) lines.Add($"Nexus could not be asked just now ({check}), so this is only what the app already knew.");

        lines.Add(string.Join("; ", clauses) + ".");
        lines.Add("The app can't tell which game build a Nexus file was made for, so read the description of a newer file before installing it.");
        return string.Join("\n\n", lines);
    }
}
