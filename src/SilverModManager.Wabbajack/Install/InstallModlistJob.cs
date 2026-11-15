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

/// <summary>
/// Installs a modlist's archive members and inline files into a loadout as mod groups under a
/// <see cref="WabbajackModlistLoadoutGroup"/>. A directive the app cannot execute leaves a
/// <see cref="WabbajackSkippedFile"/> row. Once the files are written the groups are put in the order the profile's
/// modlist.txt gives, the profile's plugin list is staged, and the list is enabled unless something was skipped.
/// Running the job again on the same loadout continues in the same group and brings it in line with the list: files the
/// list still installs are kept, ones whose content changed or that the list no longer installs are removed.
/// </summary>
/// <summary>
/// A commit ends the batch of files written to the loadout when any limit is reached. Each commit makes the loadout
/// recompute, so fewer is cheaper; the limits keep a long install from holding hours of work in one transaction, since
/// what is committed is what a later run finds and keeps.
/// </summary>
public sealed record CommitPolicy(int MaxItems, long MaxBytes, TimeSpan MaxInterval)
{
    public static readonly CommitPolicy Default = new(10_000, 2L * 1024 * 1024 * 1024, TimeSpan.FromMinutes(2));
}

public sealed partial class InstallModlistJob : IJobDefinitionWithStart<InstallModlistJob, WabbajackModlistLoadoutGroup.ReadOnly>
{
    // The compiler records the hash of these before MO2 rewrites them, so the installed copy never matches.
    private static readonly string[] HashUnchecked = ["modlist.txt", "SkyrimPrefs.ini"];

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
    public required LoadoutId TargetLoadout { get; init; }
    public required WabbajackModlistLibraryFile.ReadOnly Modlist { get; init; }
    public required InstallOptions Options { get; init; }

    public required IConnection Connection { get; init; }
    public required IFileStore FileStore { get; init; }
    public required ArchiveInventory Inventory { get; init; }
    public required IDirectiveTransforms Transforms { get; init; }
    public required IPrefixResolver PrefixResolver { get; init; }
    public required WabbajackPaths Paths { get; init; }
    public required GeneratedFileWriter Writer { get; init; }

    /// <summary>The app's temp root; the resolver spools non-seekable archive members here rather than to /tmp.</summary>
    public required TemporaryFileManager TemporaryFiles { get; init; }
    public required IReadOnlyList<INestedArchiveOpener> Openers { get; init; }
    public required ILogger Logger { get; init; }
    public required ModOrderImporter OrderImporter { get; init; }
    public required ILoadoutManager LoadoutManager { get; init; }
    public required ILibraryArchiveLocator Locator { get; init; }
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member

    /// <summary>
    /// Recognises the tools of the list the catalogue knows (xEdit, DynDOLOD, the OpenComposite Unleashed Configurator), so
    /// they run with the catalogue's prefix setup and output capture. <see cref="Create"/> takes it from the container.
    /// </summary>
    public required KnownToolDetector ToolDetector { get; init; }

    /// <summary>What the install may hold in memory at once; the budget for this machine when not set.</summary>
    public ByteBudget? MemoryBudget { get; init; }

    /// <summary>How many directives are resolved and produced at once; one per processor when not set.</summary>
    public int? Parallelism { get; init; }

    /// <summary>When the files written so far are committed to the loadout.</summary>
    public CommitPolicy Commits { get; init; } = CommitPolicy.Default;

    /// <summary>
    /// The toolbar entries of the list's ModOrganizer.ini that were not registered as tools, each with the reason, once the
    /// install has run. Entries left out on purpose (Mod Organizer itself, the game and its launcher) are not listed.
    /// </summary>
    public IReadOnlyList<string> UnregisteredTools { get; private set; } = [];

    /// <summary>How far the install is. Observe <see cref="ModlistInstallProgress.Changes"/> to follow it live.</summary>
    public ModlistInstallProgress Progress { get; } = new();

    /// <summary>Reads the memory figures the install log ends each line with. <see cref="Create"/> takes it from the container so contributors registered there are included.</summary>
    public IMemorySampler MemorySampler { get; init; } = new ProcessMemorySampler();

    public static IJobTask<InstallModlistJob, WabbajackModlistLoadoutGroup.ReadOnly> Create(
        IServiceProvider provider,
        LoadoutId target,
        WabbajackModlistLibraryFile.ReadOnly modlist,
        InstallOptions options)
    {
        var monitor = provider.GetRequiredService<IJobMonitor>();
        provider.GetRequiredService<ModlistInstallRuns>().EnsureNoneInto(target);

        var fileStore = provider.GetRequiredService<IFileStore>();
        var temporaryFiles = provider.GetRequiredService<TemporaryFileManager>();
        var job = new InstallModlistJob
        {
            TargetLoadout = target,
            Modlist = modlist,
            Options = options,
            Connection = provider.GetRequiredService<IConnection>(),
            FileStore = fileStore,
            Inventory = provider.GetRequiredService<ArchiveInventory>(),
            Transforms = provider.GetRequiredService<IDirectiveTransforms>(),
            PrefixResolver = provider.GetRequiredService<IPrefixResolver>(),
            Paths = provider.GetRequiredService<WabbajackPaths>(),
            TemporaryFiles = temporaryFiles,
            Writer = new GeneratedFileWriter(fileStore, temporaryFiles),
            Openers = provider.GetServices<INestedArchiveOpener>().ToArray(),
            Logger = provider.GetRequiredService<ILogger<InstallModlistJob>>(),
            OrderImporter = provider.GetRequiredService<ModOrderImporter>(),
            LoadoutManager = provider.GetRequiredService<ILoadoutManager>(),
            Locator = provider.GetRequiredService<ILibraryArchiveLocator>(),
            MemorySampler = provider.GetRequiredService<IMemorySampler>(),
            ToolDetector = provider.GetRequiredService<KnownToolDetector>(),
        };
        return monitor.Begin<InstallModlistJob, WabbajackModlistLoadoutGroup.ReadOnly>(job);
    }

    public async ValueTask<WabbajackModlistLoadoutGroup.ReadOnly> StartAsync(IJobContext<InstallModlistJob> context)
    {
        var cancellationToken = context.CancellationToken;
        using var progressLog = Progress.Changes.Subscribe(new InstallProgressLog(Logger, Modlist.Name, MemorySampler, TimeProvider.System, new SmapsDump(TimeProvider.System)).Report);

        // Before the check, which may record the digests of game files: a refused update writes nothing.
        var existing = FindGroup();
        var check = await CheckOnly(TargetLoadout, Modlist, Connection, FileStore, Inventory, Logger, Progress, cancellationToken);
        if (check.Archives.Missing.Count > 0 && !Options.SkipMissingArchives) throw new ArchivesMissingException(check.Archives.Missing);
        var (group, extrasFolder) = await EnsureGroup(check, existing, cancellationToken);

        try
        {
            // What an earlier run committed is read first: the directives it finished are left out of both phases.
            var state = LoadExisting(Connection.Db, group);
            await RecordFolders(state);
            // Before anything is matched against the rows: a folder the list renamed keeps its group, so its files are found under the new name.
            if (_updateMode) await RenameMovedFolders(check, state, cancellationToken);
            // A resumed update has lost InstalledAt, but its earlier version's rows are still there to tell a kept file from a new one.
            if (_groupWasInstalled || (_updateMode && _previousModlist is not null)) _gameFilesKeptByUser = await GameFilesTheUserKept(check, state, cancellationToken);
            var prepared = await Resolve(check, state, cancellationToken);
            await Write(check, group, extrasFolder, state, prepared, cancellationToken);
            await LinkArchives(check, group, state, cancellationToken);
            await RegisterTools(check, group, extrasFolder, cancellationToken);
            await Order(check, group, state, cancellationToken);
        }
        finally
        {
            if (_resolver is { } resolver)
            {
                // Hashes computed before a stop are worth keeping; the next run would otherwise read the same files again.
                try
                {
                    await resolver.FlushDigests(CancellationToken.None);
                }
                catch (Exception e)
                {
                    Logger.LogWarning(e, "Could not record the hashes computed for the library files of '{List}'", Modlist.Name);
                }

                // Stops copies still running, then releases the pins on the bytes the resolver backed up; the loadout files now reference them.
                await resolver.DisposeAsync();
            }
        }

        return WabbajackModlistLoadoutGroup.Load(Connection.Db, group.Value);
    }

    /// <summary>
    /// Records which archive each mod folder came from and links its group to the library file that holds it. Runs after
    /// <see cref="Write"/>, so the groups of folders the new list dropped are already gone, and on every run, so a resume
    /// links a folder whose archive reached the library since the run that wrote it.
    /// </summary>
    private async Task LinkArchives(Checked check, LoadoutItemGroupId group, WriteState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var db = Connection.Db;
        var main = ModGroupArchives.MainArchiveByFolder(check.Placements, check.BsaInputs, check.List.Archives);
        using var tx = Connection.BeginTransaction();
        ModGroupArchives.Record(tx, db, state.Groups, main);
        ModGroupArchives.Relink(tx, db, Locator, group.Value, main);
        // A commit makes the loadout recompute, so a list with nothing to record or link does not make one.
        if (!tx.IsEmpty) await tx.Commit();
    }

    /// <summary>The destinations of the <see cref="SkipKind.KeptOriginal"/> notes an earlier run left, which the resume deletes and so has to write again.</summary>
    private readonly HashSet<string> _keptOriginals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The destinations of every skip note an earlier run left; read before the resume deletes them.</summary>
    private readonly HashSet<string> _earlierSkips = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the group this run continues had finished installing, so a file it holds neither a row nor a note for was left alone on purpose.</summary>
    private bool _groupWasInstalled;

    /// <summary>Whether this run moves the group from an earlier version of the list: asked for, or continuing an update that stopped.</summary>
    private bool _updateMode;

    /// <summary>The folders this update renamed, so the previous profile's lines are read under the names the groups have now.</summary>
    private IReadOnlyList<(string From, string To)> _renamedFolders = [];
    private IReadOnlySet<string> _previousFolders = new HashSet<string>();

    /// <summary>The library file the group was installed from before the update, when this run is one.</summary>
    private EntityId? _previousModlist;

    /// <summary>Game-folder files the user kept on an earlier run, which a rerun without the same choice must not replace.</summary>
    private IReadOnlySet<RelativePath> _gameFilesKeptByUser = new HashSet<RelativePath>();

    private static string Fold(RelativePath path) => path.ToString().Replace('\\', '/').ToLowerInvariant();
}
