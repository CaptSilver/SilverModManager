using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Paths.Trees.Traits;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Larian.Engine.Installers;

/// <summary>
/// Installer for a Larian game's script extender, which is one DLL and the files beside it, installed next to the
/// game's engine binaries. For Baldur's Gate 3 that is
/// <see href="https://github.com/Norbyte/bg3se">BG3SE</see>
/// (<see href="https://www.nexusmods.com/baldursgate3/mods/2172">Nexus Mods page</see>).
/// </summary>
public class LarianScriptExtenderInstaller : ALibraryArchiveInstaller
{
    private readonly string _gameName;
    private readonly LarianScriptExtender _extender;

    public LarianScriptExtenderInstaller(IServiceProvider serviceProvider, string gameName, LarianScriptExtender extender) :
        base(serviceProvider, serviceProvider.GetRequiredService<ILogger<LarianScriptExtenderInstaller>>())
    {
        _gameName = gameName;
        _extender = extender;
    }

    public override ValueTask<InstallerResult> ExecuteAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction transaction,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken)
    {
        // The extender is a Windows DLL; the native build can't load it, so it would only sit next to the binaries.
        if (LarianBuild.IsNative(loadout.InstallationInstance.LocatorResult))
            return ValueTask.FromResult<InstallerResult>(new NotSupported(Reason: $"The Script Extender is a Windows DLL and cannot load into the native Linux build of {_gameName}"));

        var dllName = _extender.DllPath.FileName.ToString();
        var tree = LibraryArchiveTreeExtensions.GetTree(libraryArchive);
        var nodes = tree.FindSubPathsByKeyUpward([dllName]);
        if (!nodes.TryGetFirst(out var dllNode)) return ValueTask.FromResult<InstallerResult>(new NotSupported(Reason: $"Found no file named `{dllName}`"));

        var parent = dllNode.Parent() ?? tree;

        foreach (var fileNode in parent.EnumerateFilesBfs())
        {
            var relativePath = _extender.DllPath.Parent.Path.Join(fileNode.Value.Item.Path.DropFirst(parent.Depth()));
            _ = new LoadoutFile.New(transaction, out var id)
            {
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(transaction, id)
                {
                    TargetPath = (loadout.Id, _extender.DllPath.LocationId, relativePath),
                    LoadoutItem = new LoadoutItem.New(transaction, id)
                    {
                        Name = relativePath.Name,
                        LoadoutId = loadout.Id,
                        ParentId = loadoutGroup.Id,
                    },
                },
                Hash = fileNode.Value.Item.LibraryFile.Value.Hash,
                Size = fileNode.Value.Item.LibraryFile.Value.Size,
            };
        }

        return ValueTask.FromResult<InstallerResult>(new Success());
    }
}
