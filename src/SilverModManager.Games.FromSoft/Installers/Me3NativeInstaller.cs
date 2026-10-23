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
/// Installs an archive that ships a DLL for me3 to inject, such as a trainer or a mod loader plug-in. The whole
/// archive goes under <c>natives/&lt;id&gt;/</c> in the mods folder, laid out as shipped, because the DLL may read
/// files beside it. The profile loads the one DLL <see cref="Me3NativeDll"/> picks from that folder, so installing
/// an archive where no pick can be made is refused here instead of failing at the next apply.
/// </summary>
public class Me3NativeInstaller : ALibraryArchiveInstaller
{
    public Me3NativeInstaller(IServiceProvider serviceProvider)
        : base(serviceProvider, serviceProvider.GetRequiredService<ILogger<Me3NativeInstaller>>())
    {
    }

    public override ValueTask<InstallerResult> ExecuteAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction tx,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken)
    {
        var modId = Me3InstallerFiles.ModId(libraryArchive);
        if (Me3InstallerFiles.FolderNameRefusal(modId) is { } refusal)
            return Me3InstallerFiles.Decline(Logger, refusal);

        var files = Me3InstallerFiles.Files(libraryArchive);

        var paths = files.Select(f => f.Path).ToArray();
        if (Me3NativeDll.Find(paths, modId) is null)
        {
            var dlls = Me3NativeDll.ListDlls(paths);
            return Me3InstallerFiles.Decline(Logger, dlls.Length == 0
                ? "The archive holds no DLL for me3 to load."
                : $"Several DLLs; pick the mod's DLL with the manual installer: {string.Join(", ", dlls)}");
        }

        foreach (var file in files)
            Me3InstallerFiles.AddFile(tx, loadout, loadoutGroup, file, $"{Me3ModsLayout.NativesFolder}/{modId}/{file.Path}", file.Path);

        return ValueTask.FromResult<InstallerResult>(new Success());
    }
}
