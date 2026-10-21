using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Me3;

/// <summary>
/// The <c>.me3</c> profile as a file the synchronizer writes on every apply. The loadout is the profile: there is
/// no copy to merge with, so an edit made to the file by hand is not read back, and the next apply replaces it.
/// </summary>
public class Me3ProfileFile(ILogger<Me3ProfileFile> logger) : IIntrinsicFile
{
    public GamePath Path { get; } = new(LocationId.Me3Mods, Me3ModsLayout.ProfileFileName);

    public async Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
    {
        var text = Render(loadout, syncTree);

        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
        await writer.WriteAsync(text);
        await writer.FlushAsync();
    }

    /// <summary>
    /// The synchronizer hands over a file whose content is not what it last wrote: someone edited it, or it was
    /// written by the previous apply and the disk state recorded since is older than that. Either way the file is
    /// replaced here with the generated one, because the synchronizer only rewrites an intrinsic file whose disk
    /// state is unchanged, which would leave the edit, or a profile one apply behind, in place. The loadout is
    /// not changed: settings someone would tune by hand belong in the loadout.
    /// </summary>
    public async Task Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx)
    {
        var generated = Render(loadout, syncTree);

        using var reader = new StreamReader(stream, leaveOpen: true);
        var onDisk = await reader.ReadToEndAsync();
        if (onDisk == generated) return;

        // Replaced by renaming a new file over it: the synchronizer still holds the old one open for reading.
        var target = loadout.InstallationInstance.Locations.ToAbsolutePath(Path);
        var replacement = target.Parent / $"{target.FileName}.new";
        await replacement.WriteAllTextAsync(generated);
        await replacement.MoveToAsync(target, overwrite: true);

        logger.LogInformation("The me3 profile {Path} differed from the one the loadout generates and was replaced. Changes made to it by hand are not kept; put settings in the loadout instead", target);
    }

    private string Render(Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
    {
        var installation = loadout.InstallationInstance;
        var files = syncTree
            .Where(entry => entry.Key.LocationId == LocationId.Me3Mods && entry.Key != Path && entry.Value.HaveLoadout)
            .Select(entry => entry.Key.Path.Path);

        return Me3Profile.Render(files, installation.Locations[LocationId.Me3Mods].Path, EldenRingPaths.GameFolder(installation));
    }
}
