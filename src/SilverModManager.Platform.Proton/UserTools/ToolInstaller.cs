using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.DataModel;
using SilverModManager.Sdk.Games;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.UserTools;

/// <summary>What <see cref="ToolInstaller.Add"/> did: the tools it saved and the ones that were already saved.</summary>
public sealed record ToolInstallResult(IReadOnlyList<string> Added, IReadOnlyList<string> AlreadyAdded);

/// <summary>
/// Saves the tools at a path the user points to as rows of the loadout's installation. A folder is scanned for
/// catalogue tools; a single program is matched against the catalogue and, if nothing knows it, saved as the plain
/// program it is.
/// </summary>
public sealed class ToolInstaller(IConnection connection, KnownToolDetector detector)
{
    /// <exception cref="ToolRefusedException">The path does not exist, or a folder holds no known tool.</exception>
    public async Task<ToolInstallResult> Add(Loadout.ReadOnly loadout, AbsolutePath path)
    {
        var game = loadout.InstallationInstance.GetGame();
        IReadOnlyList<(string Name, ToolId? Id, AbsolutePath Exe)> found;
        if (path.DirectoryExists())
        {
            found = detector.Scan(game, path).Select(d => (d.Definition.DisplayName, d.Definition.Id, d.Installed.Exe)).ToArray();
            if (found.Count == 0)
                throw new ToolRefusedException($"There is no known tool in {path}. Point at the program itself to add it as it is.");
        }
        else if (path.FileExists)
        {
            var matches = detector.Match(game, path);
            found = matches.Count > 0
                ? matches.Select(d => (d.Definition.DisplayName, d.Definition.Id, d.Installed.Exe)).ToArray()
                : [(System.IO.Path.GetFileNameWithoutExtension(path.FileName), (ToolId?)null, path)];
        }
        else
        {
            throw new ToolRefusedException($"{path} does not exist.");
        }

        var existing = UserTool.FindByInstallation(connection.Db, loadout.InstallationId)
            .Select(row => (row.Exe, ToolId: row.ToolId.HasValue ? row.ToolId.Value : null))
            .ToHashSet();

        var added = new List<string>();
        var already = new List<string>();
        using var tx = connection.BeginTransaction();
        foreach (var (name, id, exe) in found)
        {
            if (existing.Contains((exe.GetFullPath(), id?.Value)))
            {
                already.Add(name);
                continue;
            }

            var row = new UserTool.New(tx)
            {
                InstallationId = loadout.InstallationId,
                Name = name,
                Exe = exe.GetFullPath(),
                Arguments = "",
                RunVia = ToolRunVia.Auto,
            };
            if (id is { } toolId) tx.Add(row.Id, UserTool.ToolId, toolId.Value);
            added.Add(name);
        }

        if (added.Count > 0) await tx.Commit();
        return new ToolInstallResult(added, already);
    }
}
