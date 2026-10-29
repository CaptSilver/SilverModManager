using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using NexusMods.Paths.Trees.Traits;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Witcher.Witcher3.Installers;

/// <summary>
/// Places a Witcher 3 mod archive by its layout: menu mods, <c>mod*</c> and <c>dlc*</c> folders at any depth, a
/// <c>Mods</c> folder anywhere in the archive, a bare or wrapped <c>content</c> folder, and root <c>bin</c> or
/// loose content files. The rules themselves are in <see cref="Witcher3InstallPlan"/>.
/// </summary>
public sealed class Witcher3ModInstaller(IServiceProvider provider)
    : ALibraryArchiveInstaller(provider, provider.GetRequiredService<ILogger<Witcher3ModInstaller>>())
{
    /// <summary>Files that ship with a mod for the reader and have no place in the game folder. A <c>.csv</c> is the source a modder compiles into <c>.w3strings</c>.</summary>
    public static readonly Extension[] DocumentationExtensions = [new(".txt"), new(".md"), new(".pdf"), new(".png"), new(".jpg"), new(".jpeg"), new(".url"), new(".nfo"), new(".csv")];

    /// <summary>The Mods folder name for an archive whose own layout names none: <c>mod</c> plus the file name without extension, spaces and dots.</summary>
    public static string ModNameFromArchive(string fileName) => ModFolderName(Path.GetFileNameWithoutExtension(fileName));

    /// <summary>A Mods folder name the game loads: <c>mod</c> plus the name without spaces and dots.</summary>
    public static string ModFolderName(string name) => "mod" + name.Replace(" ", "").Replace(".", "");

    public override ValueTask<InstallerResult> ExecuteAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction transaction,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken)
    {
        var files = LibraryArchiveTreeExtensions.GetTree(libraryArchive).Item.GetFiles<LibraryArchiveTree, RelativePath>()
            .ToDictionary(f => f.Item.Path.Path, f => f.Item.LibraryFile.Value, StringComparer.Ordinal);

        var fileName = libraryArchive.AsLibraryFile().FileName.ToString();
        var plan = Witcher3InstallPlan.Plan(files.Keys.Order(StringComparer.Ordinal).ToArray(), fileName);
        if (plan is { IsAmbiguous: true })
        {
            var variants = plan.Variants.Count > 0 ? $": {string.Join(", ", plan.Variants)}" : "";
            return ValueTask.FromResult<InstallerResult>(new NotSupported($"the archive has variants that need a choice{variants}"));
        }
        if (plan is not { Placements.Count: > 0 })
            return ValueTask.FromResult<InstallerResult>(new NotSupported("no Witcher 3 layout matches"));

        foreach (var (source, target) in plan.Placements)
        {
            var file = files[source];
            _ = new LoadoutFile.New(transaction, out var id)
            {
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(transaction, id)
                {
                    TargetPath = (loadout.Id, target.LocationId, target.Path),
                    LoadoutItem = new LoadoutItem.New(transaction, id)
                    {
                        Name = target.FileName.ToString(),
                        LoadoutId = loadout.Id,
                        ParentId = loadoutGroup.Id,
                    },
                },
                Hash = file.Hash,
                Size = file.Size,
            };
        }

        return ValueTask.FromResult<InstallerResult>(new Success());
    }
}
