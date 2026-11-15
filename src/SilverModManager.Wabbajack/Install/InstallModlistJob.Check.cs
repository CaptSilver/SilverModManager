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
    /// <summary>One directive with the place it lands in the loadout, decided once for the whole job.</summary>
    internal sealed record Placement(int Index, Directive Directive, RelativePath To, Mo2Target Target, string? Group, GamePath? Path);

    internal sealed record Checked(
        Loadout.ReadOnly Loadout,
        ModList List,
        LibraryArchive.ReadOnly ListArchive,
        ArchiveCheck Archives,
        GameFileSourceLookup GameFiles,
        IReadOnlyList<Placement> Placements,
        // TempId and path inside it, folded, to the directive that supplies the file.
        IReadOnlyDictionary<string, Dictionary<string, Placement>> BsaInputs)
    {
        public ArchivePlan Plan => Archives.Plan;
    }

    /// <summary>
    /// The check phase on its own: reads the list, classifies every directive and matches its archives against the
    /// library, writing nothing. The install job runs it first, and a read-only look at an update runs the same code.
    /// </summary>
    internal static async Task<Checked> CheckOnly(
        LoadoutId target, WabbajackModlistLibraryFile.ReadOnly modlist, IConnection connection, IFileStore fileStore,
        ArchiveInventory inventory, ILogger logger, ModlistInstallProgress progress, CancellationToken cancellationToken)
    {
        progress.Enter(InstallPhase.Check, 0, 0);

        var classified = await ClassifyList(target, modlist, connection, fileStore, cancellationToken);
        var loadout = classified.Loadout;
        var list = classified.List;
        var gameFiles = inventory.GameFilesFor(loadout);
        var plan = await inventory.Build(list, connection.Db, gameFiles, cancellationToken, program: classified.Program);

        var (estimate, isLowerBound) = EstimateExtractedSize(plan);
        progress.SetEstimate(estimate, isLowerBound);
        CountPathsNeverInstalled(progress, logger, classified.Placements, list);

        return new Checked(loadout, list, classified.ListArchive, ArchiveCheck.Of(plan), gameFiles, classified.Placements, classified.BsaInputs);
    }

    /// <summary>A list read and laid out for a loadout, before its archives are matched.</summary>
    internal sealed record Classified(
        Loadout.ReadOnly Loadout,
        ModList List,
        LibraryArchive.ReadOnly ListArchive,
        IReadOnlyList<Placement> Placements,
        IReadOnlyDictionary<string, Dictionary<string, Placement>> BsaInputs,
        Mo2ProgramArchives Program);

    /// <summary>Reads <paramref name="modlist"/> and decides where each of its directives lands in <paramref name="target"/>.</summary>
    internal static async Task<Classified> ClassifyList(
        LoadoutId target, WabbajackModlistLibraryFile.ReadOnly modlist, IConnection connection, IFileStore fileStore, CancellationToken cancellationToken)
    {
        if (!modlist.AsLibraryFile().TryGetAsLibraryArchive(out var listArchive))
            throw new UnsupportedModlistException("The modlist file is not an expanded archive, so it cannot be read");
        var list = await ModlistArchive.ReadModlist(fileStore, listArchive, cancellationToken);
        return await ClassifyList(target, list, listArchive, connection, fileStore, cancellationToken);
    }

    /// <summary>Lays out an already read <paramref name="list"/> (held in <paramref name="listArchive"/>) for <paramref name="target"/>.</summary>
    internal static async Task<Classified> ClassifyList(
        LoadoutId target, ModList list, LibraryArchive.ReadOnly listArchive, IConnection connection, IFileStore fileStore, CancellationToken cancellationToken)
    {
        var loadout = Loadout.Load(connection.Db, target);
        var installation = loadout.InstallationInstance;
        var mo2Target = RequireTarget(installation);
        _ = RequirePluginVariety(installation);
        RequireGame(installation, list);

        var unified = PathCasing.Unify(list.Directives.Select(d => d.To));
        var tos = list.Directives.Select(d => unified[d.To]).ToArray();

        var context = new Mo2Context(
            await SelectedProfile(fileStore, list, tos, listArchive, cancellationToken),
            list.Name,
            Mo2Layout.GameRootFolders(installation, mo2Target, tos),
            mo2Target.ProfileSettingsFiles(installation),
            relative => mo2Target.ModFilePath(installation, relative));

        var placements = Classify(list, tos, context);
        var bsaInputs = CollectBsaInputs(placements);

        // Mod Organizer 2's own files are the app's job: they are dropped here and its archive is not fetched.
        var program = Mo2Program.Find(list, context.GameRootFolders);
        placements = DropProgramFiles(placements, program, context.GameRootFolders);
        return new Classified(loadout, list, listArchive, placements, bsaInputs, program);
    }

    internal static IMo2InstanceTarget RequireTarget(GameInstallation installation)
    {
        var name = installation.Game.DisplayName;
        if (installation.Game is not IMo2InstanceTarget target)
            throw new UnsupportedModlistException($"Wabbajack lists for {MarkdownText.Code(name)} are not supported yet: the game cannot lay out a Mod Organizer instance");
        if (target.UnsupportedReason(installation) is { } reason)
            throw new UnsupportedModlistException($"Wabbajack lists for {MarkdownText.Code(name)} are not supported yet: {MarkdownText.Inline(reason)}");
        return target;
    }

    /// <summary>The game's one load order, which the profile's plugin list is staged on.</summary>
    internal static ISortOrderVariety RequirePluginVariety(GameInstallation installation)
    {
        var varieties = installation.GetGame().SortOrderManager.GetSortOrderVarieties();
        if (varieties.Count != 1)
            throw new UnsupportedModlistException($"Wabbajack lists for {installation.Game.DisplayName} are not supported yet: the game has {varieties.Count} load orders and a profile carries one plugin list");
        return varieties[0];
    }

    private static void RequireGame(GameInstallation installation, ModList list)
    {
        var listGame = WabbajackGames.GameIdFor(list.GameType)
            ?? throw new UnsupportedModlistException($"Wabbajack lists for {MarkdownText.Code(list.GameType)} are not supported yet: this app has no title for that game");
        if (listGame != installation.Game.GameId)
            throw new UnsupportedModlistException($"The list is for {MarkdownText.Code(list.GameType)}, but this loadout is for {installation.Game.DisplayName}");
    }

    private static async Task<string> SelectedProfile(IFileStore fileStore, ModList list, IReadOnlyList<RelativePath> tos, LibraryArchive.ReadOnly listArchive, CancellationToken cancellationToken)
    {
        for (var i = 0; i < list.Directives.Count; i++)
        {
            if (list.Directives[i] is not InlineFile ini || !tos[i].ToString().Equals("ModOrganizer.ini", StringComparison.OrdinalIgnoreCase)) continue;

            await using var stream = await ModlistArchive.ReadBlob(fileStore, listArchive, ini.SourceDataId, cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            if (Mo2Ini.SelectedProfile(Mo2Ini.Parse(await reader.ReadToEndAsync(cancellationToken))) is { Length: > 0 } selected)
                return selected;
        }

        // No selection recorded: the only profile, or the first by name.
        return tos.Select(t => t.ToString().Split('/'))
            .Where(parts => parts.Length >= 3 && parts[0].Equals("profiles", StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[1])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? "";
    }

    private static IReadOnlyList<Placement> Classify(ModList list, IReadOnlyList<RelativePath> tos, Mo2Context context)
    {
        var placements = new List<Placement>(list.Directives.Count);
        for (var i = 0; i < list.Directives.Count; i++)
        {
            var target = Mo2Layout.Classify(tos[i], context);
            var (group, path) = target switch
            {
                Mo2Target.ModFile mod => (mod.GroupName, (GamePath?)mod.Path),
                Mo2Target.ProfileSetting setting => (Mo2Layout.ProfileSettingsGroup, setting.Path),
                Mo2Target.ProfileTweak tweak => (Mo2Layout.ProfileSettingsGroup, tweak.Path),
                Mo2Target.GameRootFile root => (root.GroupName, new GamePath(LocationId.Game, root.Path)),
                _ => ((string?)null, (GamePath?)null),
            };
            placements.Add(new Placement(i, list.Directives[i], tos[i], target, group, path));
        }

        // One spelling per path inside each location, across all mods: two mods may write SKSE/ and skse/.
        var located = new List<(int Index, GamePath Path)>();
        foreach (var placement in placements)
        {
            if (placement.Path is { } path) located.Add((placement.Index, path));
        }

        var unified = new Dictionary<int, GamePath>();
        foreach (var byLocation in located.GroupBy(l => l.Path.LocationId))
        {
            var spelling = PathCasing.Unify(byLocation.Select(l => l.Path.Path));
            foreach (var (index, path) in byLocation) unified[index] = new GamePath(byLocation.Key, spelling[path.Path]);
        }

        return placements.Select(p => unified.TryGetValue(p.Index, out var path) ? p with { Path = path } : p).ToArray();
    }

    /// <summary>
    /// A file taken from a Mod Organizer 2 release that lands in the program or a tool folder goes nowhere, even when
    /// the release is kept for another file: the app never installs the program.
    /// </summary>
    private static IReadOnlyList<Placement> DropProgramFiles(IReadOnlyList<Placement> placements, Mo2ProgramArchives program, IReadOnlySet<string> gameRootFolders)
        => placements.Select(p => p.Directive is FromArchive from
                                  && program.IsRecognised(from.Source.ArchiveXxHash64)
                                  && Mo2Program.IsProgramLocation(p.To, gameRootFolders)
            ? p with { Target = new Mo2Target.Dropped("Mod Organizer 2 program file"), Group = null, Path = null }
            : p).ToArray();

    private static Dictionary<string, Dictionary<string, Placement>> CollectBsaInputs(IReadOnlyList<Placement> placements)
    {
        var inputs = new Dictionary<string, Dictionary<string, Placement>>();
        foreach (var placement in placements)
        {
            if (placement.Target is not Mo2Target.BsaInput input) continue;
            if (!inputs.TryGetValue(Fold(input.TempId), out var files)) inputs[Fold(input.TempId)] = files = new();
            files[Fold(input.Path)] = placement;
        }
        return inputs;
    }

    /// <summary>
    /// The sum of what the library holds for each archive, since the library keeps every extracted member and not the
    /// download. An archive it does not hold yet counts by its download size, which is the least it can come to.
    /// </summary>
    private static (Size Estimate, bool IsLowerBound) EstimateExtractedSize(ArchivePlan plan)
    {
        ulong total = 0;
        var isLowerBound = false;
        foreach (var row in plan.Rows)
        {
            switch (row.State)
            {
                case ArchiveState.Have:
                    var library = row.Library ?? throw new InvalidOperationException($"The plan has '{row.Archive.Name}' in the library but names no library file for it");
                    total += library.TryGetAsLibraryArchive(out var archive)
                        ? archive.Children.Aggregate(0UL, (sum, entry) => sum + entry.AsLibraryFile().Size.Value)
                        : library.Size.Value;
                    break;
                case ArchiveState.GameFile or ArchiveState.GameFileVersion or ArchiveState.NotNeeded:
                    break;
                default:
                    total += (ulong)row.Archive.Size;
                    isLowerBound = true;
                    break;
            }
        }
        return (Size.From(total), isLowerBound);
    }

    private static void CountPathsNeverInstalled(ModlistInstallProgress progress, ILogger logger, IReadOnlyList<Placement> placements, ModList list)
    {
        progress.AddDropped(placements.Count(p => p.Target is Mo2Target.Seed or Mo2Target.Dropped));
        progress.AddExtras(placements.Count(p => p.Target is Mo2Target.Extra));

        // Files for an archive no CreateBsa directive builds go nowhere either.
        var built = list.Directives.OfType<CreateBsa>().Select(b => Fold(b.TempId)).ToHashSet();
        var unbuilt = placements.Where(p => p.Target is Mo2Target.BsaInput input && !built.Contains(Fold(input.TempId))).ToArray();
        progress.AddDropped(unbuilt.Length);
        foreach (var group in unbuilt.GroupBy(p => ((Mo2Target.BsaInput)p.Target).TempId.ToString()))
        {
            logger.LogWarning("Dropped {Count} file(s) for archive '{TempId}' of '{List}' because no directive builds it: {Inputs}",
                group.Count(), group.Key, list.Name, string.Join(", ", group.Select(p => p.To.ToString()).Take(SkipExamplesLogged)));
        }
    }
}
