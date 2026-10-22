using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Paths.Trees.Traits;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.Unity.BepInEx;

/// <summary>
/// Installs a BepInEx plugin package: each file goes where <see cref="BepInExLayout"/> routes it, below a folder named
/// after the package. A loader pack is left to <see cref="BepInExPackInstaller"/>, which the loader lists first; this
/// installer refuses one so a pack that slipped past it is not scattered across the plugin folders.
/// </summary>
public sealed class BepInExPluginInstaller(IServiceProvider serviceProvider, BepInExLoader loader)
    : ALibraryArchiveInstaller(serviceProvider, serviceProvider.GetRequiredService<ILogger<BepInExPluginInstaller>>())
{
    private const string ManifestFile = "manifest.json";

    private readonly IFileStore _fileStore = serviceProvider.GetRequiredService<IFileStore>();

    public override async ValueTask<InstallerResult> ExecuteAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction tx,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken)
    {
        var files = LibraryArchiveTreeExtensions.GetTree(libraryArchive).GetFiles().ToArray();
        if (files.Length == 0)
            return new NotSupported(Reason: "The archive has no files.");

        var paths = files.Select(f => f.Item.Path).ToArray();
        var itemName = libraryArchive.AsLibraryFile().AsLibraryItem().Name;

        if (BepInExLayout.IsLoaderPack(paths, out _) || loader.IsLoaderPackName(itemName))
        {
            return new NotSupported(Reason:
                $"The archive is the {loader.LoaderPackage} loader pack, which the loader pack installer installs, not a plugin.");
        }

        var packageFolder = await BepInExPackageFolder.FromLibraryItemName(itemName, async () =>
        {
            var manifest = files.FirstOrDefault(f => f.Item.Path.ToString().Equals(ManifestFile, StringComparison.OrdinalIgnoreCase));
            return manifest is null ? null : await ReadManifest(manifest.Item.LibraryFile.Value.Hash, cancellationToken);
        });

        IReadOnlyDictionary<RelativePath, GamePath> routes;
        try
        {
            routes = BepInExLayout.Route(paths, packageFolder);
        }
        catch (BepInExLayoutException e)
        {
            return new NotSupported(Reason: e.Message);
        }

        foreach (var file in files)
            file.ToLoadoutFile(loadout.Id, loadoutGroup.Id, tx, routes[file.Item.Path]);

        return new Success();
    }

    // A manifest that does not parse is treated as absent: the file name still gives the package a folder.
    private async ValueTask<BepInExPackageFolder.Manifest?> ReadManifest(NexusMods.Hashing.xxHash3.Hash hash, CancellationToken cancellationToken)
    {
        var bytes = await _fileStore.Load(hash, cancellationToken);
        try
        {
            // Thunderstore's packaging tools often write a UTF-8 byte order mark, which JsonDocument rejects.
            var bom = Encoding.UTF8.Preamble;
            var content = bytes.AsMemory(bytes.AsSpan().StartsWith(bom) ? bom.Length : 0);
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            return new BepInExPackageFolder.Manifest(
                StringProperty(document.RootElement, "namespace"),
                StringProperty(document.RootElement, "name"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StringProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
