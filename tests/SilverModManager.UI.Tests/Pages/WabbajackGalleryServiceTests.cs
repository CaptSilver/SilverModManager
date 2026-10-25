using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.App.UI.Pages.WabbajackGallery;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.UI.Tests.Pages;

public class WabbajackGalleryServiceTests(IServiceProvider provider) : AUiTest(provider)
{
    private async Task<LibraryFileId> AddModlistFile(string name, string? galleryUrl)
    {
        using var tx = Connection.BeginTransaction();
        var bytes = System.Text.Encoding.UTF8.GetBytes(name);
        var file = new LibraryFile.New(tx, out var fileId)
        {
            FileName = $"{name}.wabbajack",
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, fileId) { Name = $"{name}.wabbajack" },
        };
        _ = new WabbajackModlistLibraryFile.New(tx, fileId)
        {
            LibraryFile = file,
            Name = name,
            Author = "Someone",
            Version = "1.0",
            GameType = "SkyrimSpecialEdition",
            ArchiveCount = 0,
            ArchiveSize = Size.Zero,
            InstallSize = Size.Zero,
            IsNsfw = false,
        };
        if (galleryUrl is not null) tx.Add(fileId, WabbajackModlistLibraryFile.GalleryDownloadUrl, galleryUrl);
        var result = await tx.Commit();
        return LibraryFileId.From(result[fileId]);
    }

    [Fact]
    public async Task ListsInLibrary_FindsTheListsDownloadedFromTheGalleryByTheirAddress()
    {
        const string url = "https://authored-files.wabbajack.org/from-gallery.wabbajack";
        var fromGallery = await AddModlistFile("From gallery", url);
        var byHand = await AddModlistFile("Added by hand", galleryUrl: null);

        var lists = Provider.GetRequiredService<IWabbajackGalleryService>().ListsInLibrary();

        lists.Should().ContainKey(url).WhoseValue.Should().Be(fromGallery);
        lists.Values.Should().NotContain(byHand);
    }
}
