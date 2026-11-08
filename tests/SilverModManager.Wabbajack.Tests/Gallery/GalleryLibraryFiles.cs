using NexusMods.Paths;
using SilverModManager.Abstractions.Library;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Gallery;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Tests.Fixtures;

namespace SilverModManager.Wabbajack.Tests.Gallery;

/// <summary>Adds real modlist files to the library and sets the gallery fields a test needs.</summary>
internal static class GalleryLibraryFiles
{
    /// <summary>The address a file added with <c>fromGallery</c> was fetched from, shaped like the CDN's release links (a uuid suffix).</summary>
    public static readonly Uri OldDownloadUrl = new("https://authored-files.wabbajack.org/old.wabbajack_00000000-0000-0000-0000-000000000001");

    public static async Task<WabbajackModlistLibraryFile.ReadOnly> Add(
        ILibraryService libraryService, TemporaryFileManager temporaryFileManager, IConnection connection, string name, string? author = null,
        string? version = null, string gameType = "SkyrimSpecialEdition", string? fileName = null, bool fromGallery = false, Uri? downloadUrl = null)
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var list = WabbajackFixture.EmptyList(name) with { GameType = gameType, Version = version ?? "1.0.0.0", Author = author ?? "" };
        var path = WabbajackFixture.Write(folder.Path, list, fileName: fileName ?? $"{Guid.NewGuid():N}.wabbajack");
        var added = await libraryService.AddLocalFile(path, UnassignedAcquisition.Instance);
        added.AsLibraryFile().TryGetAsWabbajackModlistLibraryFile(out var model);
        if (!fromGallery) return model;

        using var tx = connection.BeginTransaction();
        tx.Add(model.Id, WabbajackModlistLibraryFile.GalleryDownloadUrl, (downloadUrl ?? OldDownloadUrl).ToString());
        var result = await tx.Commit();
        return WabbajackModlistLibraryFile.Load(result.Db, model.Id);
    }

    /// <summary>Records the identity a gallery download writes, with the given values.</summary>
    public static async Task<WabbajackModlistLibraryFile.ReadOnly> WithIdentity(
        IConnection connection, WabbajackModlistLibraryFile.ReadOnly file, string machineUrl, string repository, DateTimeOffset? dateUpdated = null)
    {
        using var tx = connection.BeginTransaction();
        tx.Add(file.Id, WabbajackModlistLibraryFile.MachineUrl, machineUrl);
        tx.Add(file.Id, WabbajackModlistLibraryFile.Repository, repository);
        if (dateUpdated is { } date) tx.Add(file.Id, WabbajackModlistLibraryFile.GalleryDateUpdated, date);
        var result = await tx.Commit();
        return WabbajackModlistLibraryFile.Load(result.Db, file.Id);
    }

    public static GalleryEntry Entry(string title, string author = "Someone", string machineUrl = "a-list", string repository = "wabbajack-tools/mod-lists",
        string game = "skyrimspecialedition") => new()
    {
        Title = title,
        Author = author,
        GalleryGame = game,
        MachineUrl = machineUrl,
        Repository = repository,
    };

    public static GalleryListing Listing(params GalleryEntry[] entries) => new(entries, DateTimeOffset.UnixEpoch, IsStale: false);
}
