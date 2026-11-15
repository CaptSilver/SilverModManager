using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Channels;
using SilverModManager.Sdk.Diagnostics;
using SilverModManager.Sdk.Hashes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Games.Modlists;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.BuiltInEntities;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.ToolRunner;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Wabbajack.Mapping;
using SilverModManager.Wabbajack.Models;
using BethesdaArchiveException = SilverModManager.Games.Bethesda.Archives.BethesdaArchiveException;
using FileState = SilverModManager.Games.Bethesda.Archives.FileState;
using Ba2Dx10FileState = SilverModManager.Games.Bethesda.Archives.Ba2Dx10FileState;
using PatchException = SilverModManager.Wabbajack.Transforms.PatchException;
using UnreadableTextureException = SilverModManager.Wabbajack.Transforms.UnreadableTextureException;
using UnsupportedTextureFormatException = SilverModManager.Wabbajack.Transforms.UnsupportedTextureFormatException;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Wabbajack.Install;

public sealed partial class InstallModlistJob
{
    /// <summary>
    /// Registers the executables the list's ModOrganizer.ini names as tools of the installation, once their files are on
    /// disk. A tool that is already registered is left as it is, so installing again adds nothing.
    /// </summary>
    private async Task RegisterTools(Checked check, LoadoutItemGroupId modlistGroup, AbsolutePath extrasFolder, CancellationToken cancellationToken)
    {
        var iniText = await ReadModOrganizerIni(check, cancellationToken);
        if (iniText is null) return;

        var installation = check.Loadout.InstallationInstance;
        var extras = new ExtrasFolder(extrasFolder);
        var gameRoot = installation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, RelativePath.Empty));
        var primary = installation.Game.GetPrimaryFile(installation);

        var skips = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in WabbajackSkippedFile.FindByGroup(Connection.Db, modlistGroup))
            skips[row.To] = row.Reason;
        var skipped = skips.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var switchedOff = await GroupsSwitchedOffByProfile(check, cancellationToken);
        var found = ModlistTools.Find(iniText, extras.Root, gameRoot, primary.FileName.ToString(), relative => AppliedPath(check, relative, skipped, switchedOff),
            relative => skips.GetValueOrDefault(relative), StockGameFolders(check), ListFolders(check));
        UnregisteredTools = found.Unregistered;
        foreach (var line in found.Unregistered) Logger.LogWarning("Not registering a tool of '{List}': {Reason}", check.List.Name, line);

        var db = Connection.Db;
        var known = UserTool.FindByInstallation(db, check.Loadout.InstallationId)
            .Select(t => (t.Name, t.Exe, t.Arguments)).ToHashSet();

        var game = installation.GetGame();
        using var tx = Connection.BeginTransaction();
        var any = false;
        foreach (var tool in found.Tools.Where(t => !known.Contains((t.Name, t.Exe, t.Arguments))))
        {
            var row = new UserTool.New(tx)
            {
                InstallationId = check.Loadout.InstallationId,
                Name = tool.Name,
                Exe = tool.Exe,
                Arguments = tool.Arguments,
                RunVia = ToolRunVia.Auto,
            };
            if (tool.WorkingDirectory is { } startIn) tx.Add(row.Id, UserTool.WorkingDirectory, startIn);
            // The row keeps the list's own arguments: a line on the row wins over the definition's, so only the
            // catalogue's prefix setup and output capture are added.
            var match = ToolDetector.Match(game, FileSystem.Shared.FromUnsanitizedFullPath(tool.Exe)).FirstOrDefault();
            if (match?.Definition.Id is { } toolId) tx.Add(row.Id, UserTool.ToolId, toolId.Value);
            any = true;
        }
        if (any) await tx.Commit();
    }

    /// <summary>
    /// Where Apply puts the list's file at <paramref name="relative"/> (a path inside the Mod Organizer instance), for the
    /// files the loadout deploys: game-root folders land in the game folder and mod files at their mapped place. Null for
    /// anything else, including a file this run skipped and a file of a mod the profile's modlist.txt switches off: Apply
    /// never writes either, so a tool pointing at them would not start.
    /// </summary>
    private static string? AppliedPath(Checked check, string relative, IReadOnlySet<string> skipped, IReadOnlySet<string> switchedOff)
    {
        var placement = check.Placements.FirstOrDefault(p =>
            p.Target is Mo2Target.GameRootFile or Mo2Target.ModFile
            && p.Path is not null
            && string.Equals(p.To.ToString(), relative, StringComparison.OrdinalIgnoreCase));
        if (placement?.Path is not { } path) return null;
        if (skipped.Contains(placement.To.ToString()) || placement.Group is { } group && switchedOff.Contains(group)) return null;

        return check.Loadout.InstallationInstance.Locations.ToAbsolutePath(path).ToString();
    }

    /// <summary>The names of the groups the profile's modlist.txt gives a '-' line, which <see cref="Order"/> switches off.</summary>
    private async Task<IReadOnlySet<string>> GroupsSwitchedOffByProfile(Checked check, CancellationToken cancellationToken)
    {
        var texts = await ReadProfileTexts(FileStore, check.Placements, check.ListArchive, cancellationToken);
        if (texts.ModlistTxt is null) return new HashSet<string>();

        return ModlistTxt.Parse(texts.ModlistTxt)
            .Where(e => !e.IsSeparator && !e.IsForeign && !e.Enabled)
            .Select(e => e.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string?> ReadModOrganizerIni(Checked check, CancellationToken cancellationToken)
    {
        foreach (var placement in check.Placements)
        {
            if (placement.Target is not Mo2Target.Seed { Kind: SeedKind.ModOrganizerIni } || placement.Directive is not InlineFile inline) continue;

            await using var stream = await ModlistArchive.ReadBlob(FileStore, check.ListArchive, inline.SourceDataId, cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken);
        }
        return null;
    }
}
