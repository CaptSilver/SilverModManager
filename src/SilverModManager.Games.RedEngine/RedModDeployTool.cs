using System.Reflection;
using DynamicData.Kernel;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.RedEngine.Cyberpunk2077.SortOrder;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using R3;
using static SilverModManager.Games.RedEngine.Constants;

namespace SilverModManager.Games.RedEngine;

public class RedModDeployTool : ITool
{
    private readonly IToolRunner _toolRunner;
    private readonly IPrefixResolver _prefixResolver;
    private readonly TemporaryFileManager _temporaryFileManager;
    private readonly ILogger _logger;
    private readonly RedModSortOrderVariety _redModSortOrderVariety;

    public RedModDeployTool(IToolRunner toolRunner, IPrefixResolver prefixResolver, TemporaryFileManager temporaryFileManager, ILogger<RedModDeployTool> logger, RedModSortOrderVariety sortOrderVariety)
    {
        _logger = logger;
        _toolRunner = toolRunner;
        _prefixResolver = prefixResolver;
        _temporaryFileManager = temporaryFileManager;
        _redModSortOrderVariety = sortOrderVariety;
    }
    
    public IEnumerable<GameId> GameIds => [Cyberpunk2077.Cyberpunk2077Game.GameId];

    public async Task Execute(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        var deployFolder = loadout.InstallationInstance.Locations.ToAbsolutePath(RedModDeployFolder);

        await using var loadorderFile = _temporaryFileManager.CreateFile();
        await WriteLoadOrderFile(loadorderFile.Path, loadout);
        
        // RedMod deploys to this folder and freaks out if it doesn't exist, but our synchronizer will
        // delete it if it's empty, so we need to recreate it here.
        if (!deployFolder.DirectoryExists())
            deployFolder.CreateDirectory();

        // Note (halgari): When we change the redmod order, we need to force redmod to re-run. This is due to a bug in redmod
        // not recognizing order as a change in the mod configuration. If that bug ever gets fixed (does an order check) then  
        // we can resolve this by removing the -force flag.
        var installation = loadout.InstallationInstance;
        var gameName = installation.Game.DisplayName;

        // redmod.exe only runs inside a Wine prefix, so without a way to run it, returning quietly would
        // leave the RedMods undeployed with no sign why.
        if (!_toolRunner.CanRun(installation, out var cannotRunReason))
            throw new InvalidOperationException($"Cannot deploy RedMods for {gameName}: {cannotRunReason}");

        var prefix = _prefixResolver.Resolve(installation);
        if (!prefix.IsResolved)
            throw new InvalidOperationException($"Cannot deploy RedMods for {gameName}: {prefix.Diagnostic ?? "this installation does not run in a Wine prefix."}");

        var cDriveDirectory = prefix.Prefix.Path.Combine("drive_c");

        if (!cDriveDirectory.DirectoryExists()) cDriveDirectory.CreateDirectory();
        var tmpFile = cDriveDirectory.Combine("modlist.txt");
        await using (var input = loadorderFile.Path.Read())
        await using (var output = tmpFile.Open(FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            await input.CopyToAsync(output, cancellationToken: cancellationToken);
        }

        // The script cds into %STEAM_APP_PATH% to find redMod.exe. Steam's launcher sets it, ours does not.
        var env = new Dictionary<string, string>
        {
            ["STEAM_APP_PATH"] = installation.Locations[LocationId.Game].Path.ToString(),
        };

        await using var batchPath = await ExtractTemporaryDeployScript();
        var result = await _toolRunner.RunWindowsTool(
            installation,
            batchPath.Path,
            ["deploy", "-force", "-modlist=C:\\modlist.txt"],
            env,
            logOutput: true,
            cancellationToken
        );

        // The tool runner reports the exit code rather than throwing, and both the script's own checks and
        // a failed redmod deploy end in a non-zero exit, so the error has to surface here.
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Cannot deploy RedMods for {gameName}: redmod finished with exit code {result.ExitCode}; see the process logs for its output.");
    }

    internal async Task WriteLoadOrderFile(AbsolutePath loadorderFilePath, Loadout.ReadOnly loadout)
    {
        // TODO: this currently uses the loadout sort order, change this to use the "Active" sort order,
        // once we support switching between collection and loadout sort orders
        var collectionId = Optional.None<CollectionGroupId>();
        
        var output = string.Empty;
        // Note(AL12rs): this will get the Load Order for the specific revision of the DB of loadout, which might not be the latest
        var order = _redModSortOrderVariety.GetRedModOrder(loadout, collectionId, loadout.Db);
        
        // NOTE(erri120): redmod only accepts CRLR line breaks, everything else breaks the program
        // and results in getting errors like `Non-existant mod selected`
        output = order.Count > 0 ? string.Join("\r\n", order) : string.Empty;
        
        await loadorderFilePath.WriteAllTextAsync(output);
    }
    public string Name => "RedMod Deploy";

    public IJobTask<ITool, Unit> StartJob(Loadout.ReadOnly loadout, IJobMonitor monitor, CancellationToken cancellationToken)
    {
        return monitor.Begin<ITool, Unit>(this, async _ =>
        {
            await Execute(loadout, cancellationToken);
            return Unit.Default;
        });
    }

    private async Task<TemporaryPath> ExtractTemporaryDeployScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "deploy_redmod.bat";

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new InvalidOperationException($"Resource {resourceName} not found in assembly.");

        using var reader = new StreamReader(stream);
        var file = _temporaryFileManager.CreateFile((Extension?)".bat");
        await file.Path.WriteAllTextAsync(await reader.ReadToEndAsync());
        return file;
    }
}
