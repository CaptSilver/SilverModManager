using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using NexusMods.Paths.Trees;
using NexusMods.Paths.Trees.Traits;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Unreal;

/// <summary>
/// Installs an archive by asking the game's <see cref="UnrealModLayout"/> where each file goes. A readme, picture or
/// shortcut beside the mod is left out. Any other file the layout has no rule for makes the archive unsupported: it
/// could be half of a mod for something the layout does not cover, and installing the rest would hide that.
/// </summary>
public sealed class UnrealModInstaller(IServiceProvider serviceProvider, UnrealModLayout layout)
    : ALibraryArchiveInstaller(serviceProvider, serviceProvider.GetRequiredService<ILogger<UnrealModInstaller>>())
{
    private static readonly Extension[] DocumentExtensions =
    [
        new(".txt"), new(".md"), new(".pdf"), new(".png"), new(".jpg"), new(".jpeg"), new(".url"), new(".html"),
    ];

    public override ValueTask<InstallerResult> ExecuteAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction transaction,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken)
    {
        var files = LibraryArchiveTreeExtensions.GetTree(libraryArchive).GetFiles().ToArray();

        var routed = new List<(KeyedBox<RelativePath, LibraryArchiveTree> File, GamePath Target)>();
        foreach (var file in files)
        {
            var path = file.Item.Path;
            if (layout.TryRoute(path, out var target))
                routed.Add((file, target));
            else if (!DocumentExtensions.Contains(path.Extension))
                return ValueTask.FromResult<InstallerResult>(new NotSupported(Reason:
                    $"The Unreal layout has no place for {path}: expected paks, LogicMods or UE4SS mods."));
        }

        if (routed.Count == 0)
            return ValueTask.FromResult<InstallerResult>(new NotSupported(Reason: "The archive holds no pak, LogicMods or UE4SS files."));

        foreach (var (file, target) in routed)
            file.ToLoadoutFile(loadout, loadoutGroup, transaction, target);

        return ValueTask.FromResult<InstallerResult>(new Success());
    }
}
