using NexusMods.Paths;

namespace SilverModManager.Platform.Proton.Steam.Shortcuts;

/// <summary>
/// Writes the entry into <c>shortcuts.vdf</c> with Steam closed, then the grid art. A grid failure or Steam
/// starting mid-write does not undo the entry; it comes back as <see cref="SteamShortcutWrite.Warning"/>.
/// </summary>
public sealed class ShortcutsFileRoute(ShortcutsVdfWriter writer) : ISteamShortcutRoute
{
    public string Name => "file";

    public Task<SteamShortcutWrite> AddAsync(SteamShortcutRequest request, CancellationToken token)
    {
        var entry = SteamShortcutEntry.ForLauncher(request.ShimPath.ToString(), request.IconPath.ToString());
        var file = SteamAccounts.ConfigDirectory(request.SteamRoot, request.Account.AccountId).Combine("shortcuts.vdf");

        var edit = writer.AddOrReplace(file, entry, request.Match ?? new ShortcutMatch(entry.AppId, new HashSet<string> { entry.Exe }));
        if (edit.Refusal is { } refusal) throw new SteamShortcutRefusedException(refusal, removing: false);

        string? warning = edit.Note;
        IReadOnlyList<AbsolutePath> files = [];
        try { files = SteamGridArt.Write(SteamAccounts.ConfigDirectory(request.SteamRoot, request.Account.AccountId).Combine("grid"), entry.AppId); }
        catch (IOException ex) { warning = $"The entry was added but these grid images could not be written: {ex.Message}"; }

        return Task.FromResult(new SteamShortcutWrite(entry.AppId, Confirmed: true, files, warning));
    }
}
