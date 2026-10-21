using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Installers;

/// <summary>
/// Recognises a Seamless Co-op release (<c>ersc_launcher.exe</c> beside a <c>SeamlessCoop</c> folder with
/// <c>ersc.dll</c> and <c>ersc_settings.ini</c>) and puts the folder in the overlay's config layer, so the DLL and
/// its settings sit side by side at <c>Game/SeamlessCoop/</c> for the length of a session. me3 injects the DLL, so
/// the launcher is not installed. The settings file is the template as shipped: the co-op password is added when
/// the overlay is written, never here, because loadout files are stored, backed up and exported.
/// </summary>
public class SeamlessCoopInstaller : ALibraryArchiveInstaller
{
    private readonly ILogger<SeamlessCoopInstaller> _logger;

    public SeamlessCoopInstaller(IServiceProvider serviceProvider)
        : base(serviceProvider, serviceProvider.GetRequiredService<ILogger<SeamlessCoopInstaller>>())
    {
        _logger = serviceProvider.GetRequiredService<ILogger<SeamlessCoopInstaller>>();
    }

    public override ValueTask<InstallerResult> ExecuteAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction tx,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken)
    {
        var files = Me3InstallerFiles.Files(libraryArchive);

        var dllSuffix = $"{Me3ModsLayout.SeamlessCoopFolder}/{Me3ModsLayout.ErscDll}";
        var dll = files.FirstOrDefault(f => IsAtFolderBoundary(f.Path, dllSuffix));
        if (dll.Path is null)
            return ValueTask.FromResult<InstallerResult>(new NotSupported(Reason: "The archive has no SeamlessCoop/ersc.dll, so it is not a Seamless Co-op release."));

        var folder = dll.Path[..^Me3ModsLayout.ErscDll.Length];
        var settingsPath = folder + Me3ModsLayout.ErscSettings;
        if (!files.Any(f => f.Path.Equals(settingsPath, StringComparison.OrdinalIgnoreCase)))
            return ValueTask.FromResult<InstallerResult>(new NotSupported(Reason: "The archive has SeamlessCoop/ersc.dll but no ersc_settings.ini beside it, so it is not a Seamless Co-op release."));

        foreach (var file in files)
        {
            if (!file.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            {
                if (file.Path.EndsWith(Me3ModsLayout.ErscLauncher, StringComparison.OrdinalIgnoreCase))
                    _logger.LogDebug("Not installing {Launcher}: me3 injects the Seamless Co-op DLL itself", file.Path);
                continue;
            }

            var inFolder = file.Path[folder.Length..];
            Me3InstallerFiles.AddFile(tx, loadout, loadoutGroup, file, Me3ModsLayout.OverlayPath($"{Me3ModsLayout.SeamlessCoopFolder}/{inFolder}"), inFolder);
        }

        return ValueTask.FromResult<InstallerResult>(new Success());
    }

    private static bool IsAtFolderBoundary(string path, string suffix)
        => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
           && (path.Length == suffix.Length || path[path.Length - suffix.Length - 1] == '/');
}
