using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FromSoft.Diagnostics;
using SilverModManager.Games.FromSoft.Heal;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Doctor;

/// <summary>
/// Warns about files in the real game folder that Steam's manifest for the installed build does not list or lists
/// differently, and about files only mods put there. <see cref="HealEmitter"/> sees a changed regulation only against
/// the build stamp, so a regulation that was already modded when the app first saw the game is found here. Says nothing
/// when no manifest of the install can be read.
/// </summary>
public class SteamFolderEmitter(IManifestVouching vouching, SteamFolderCheck check) : IFromSoftDiagnosticEmitter
{
    // A folder of the wrong build lists a file per line; the first ones say what is wrong.
    private const int MaxListed = 15;

    // Reads the disk, not the sync tree, so it gives the same answer without one.
    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        => Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, cancellationToken);

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var installation = loadout.InstallationInstance;
        if (installation.Game is not IFromSoftGame game) yield break;
        if (vouching.InstalledFiles(installation) is not { } manifest) yield break;

        var found = await check.Check(game.Definition, installation.Locations[LocationId.Game].Path, manifest, cancellationToken);
        if (found.IsEmpty) yield break;

        var lines = found.Changed.Select(f => $"- {f} (differs from Steam's)")
            .Concat(found.ModFiles.Select(f => $"- {f} (put there by mods)"))
            .ToArray();
        var shown = lines.Take(MaxListed).ToList();
        if (lines.Length > MaxListed) shown.Add($"- and {lines.Length - MaxListed} more");
        yield return FromSoftDiagnostics.CreateFolderDiffersFromSteam(game.DisplayName, string.Join("\n", shown));
    }
}
