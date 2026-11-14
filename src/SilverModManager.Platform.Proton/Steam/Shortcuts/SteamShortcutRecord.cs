using System.Text.Json;
using NexusMods.Paths;

namespace SilverModManager.Platform.Proton.Steam.Shortcuts;

/// <summary>
/// What this app put into Steam, kept in its own data folder so removal knows exactly what to delete and status
/// can tell an entry added through Steam's URL route that has not shown up in the file yet.
/// </summary>
/// <param name="AppId">Null until the id Steam stored is known.</param>
/// <param name="Confirmed">Whether the entry has been seen in <c>shortcuts.vdf</c>.</param>
/// <param name="Written">The grid and icon files written for it.</param>
public sealed record SteamShortcutRecord(uint AccountId, uint? AppId, string Exe, bool Confirmed, IReadOnlyList<string> Written)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>
    /// <c>silvermod/steam-shortcut.json</c> under the XDG data home.
    /// </summary>
    public static AbsolutePath PathIn(AbsolutePath dataHome) => dataHome.Combine("silvermod").Combine("steam-shortcut.json");

    /// <summary>
    /// Writes beside the file and renames over it, so a crash never leaves a half record.
    /// </summary>
    public void Save(AbsolutePath path)
    {
        path.Parent.CreateDirectory();
        var temp = path.ToString() + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
        File.Move(temp, path.ToString(), overwrite: true);
    }

    /// <summary>
    /// Null when there is no record or it cannot be read.
    /// </summary>
    public static SteamShortcutRecord? Load(AbsolutePath path)
    {
        if (!path.FileExists) return null;
        try { return JsonSerializer.Deserialize<SteamShortcutRecord>(File.ReadAllText(path.ToString())); }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    public static void Delete(AbsolutePath path)
    {
        if (path.FileExists) File.Delete(path.ToString());
    }
}
