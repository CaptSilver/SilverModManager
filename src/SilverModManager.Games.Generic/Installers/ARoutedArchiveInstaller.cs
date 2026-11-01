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

namespace SilverModManager.Games.Generic.Installers;

/// <summary>
/// Installs an archive by asking the game where each file goes. A readme, picture or shortcut beside the mod is left
/// out. Any other file the game has no rule for makes the archive unsupported: it could be half of a mod for something
/// the game does not cover, and installing the rest would hide that.
/// </summary>
public abstract class ARoutedArchiveInstaller(IServiceProvider serviceProvider, ILogger logger)
    : ALibraryArchiveInstaller(serviceProvider, logger)
{
    private static readonly Extension[] DocumentExtensions =
    [
        new(".txt"), new(".md"), new(".pdf"), new(".png"), new(".jpg"), new(".jpeg"), new(".url"), new(".html"),
    ];

    /// <summary>What the game's mods are made of, for the refusal text: "paks, LogicMods or UE4SS mods".</summary>
    protected abstract string ExpectedFiles { get; }

    /// <summary>Where <paramref name="path"/>, as laid out in the archive, belongs; false when the game has no rule for it.</summary>
    protected abstract bool TryRoute(IReadOnlyList<RelativePath> archive, RelativePath path, out GamePath target);

    public sealed override ValueTask<InstallerResult> ExecuteAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction transaction,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken)
    {
        var files = LibraryArchiveTreeExtensions.GetTree(libraryArchive).GetFiles().ToArray();
        var archive = files.Select(file => file.Item.Path).ToArray();

        var routed = new List<(KeyedBox<RelativePath, LibraryArchiveTree> File, GamePath Target)>();
        foreach (var file in files)
        {
            var path = file.Item.Path;
            if (TryRoute(archive, path, out var target))
                routed.Add((file, target));
            else if (!DocumentExtensions.Contains(path.Extension))
                return ValueTask.FromResult<InstallerResult>(new NotSupported(Reason: $"{path} has no place in this game: expected {ExpectedFiles}."));
        }

        if (routed.Count == 0)
            return ValueTask.FromResult<InstallerResult>(new NotSupported(Reason: $"The archive holds none of the files this game takes: {ExpectedFiles}."));

        foreach (var (file, target) in routed)
            file.ToLoadoutFile(loadout, loadoutGroup, transaction, target);

        return ValueTask.FromResult<InstallerResult>(new Success());
    }
}
