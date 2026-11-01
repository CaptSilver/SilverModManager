using Avalonia.Input;
using Avalonia.Platform.Storage;
using FluentAssertions;
using NSubstitute;
using SilverModManager.App.UI.Controls.DragAndDrop;
using SilverModManager.Sdk.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.UI.Tests.Controls.DragAndDrop;

public class DragPayloadsTests
{
    private static IStorageItem FileAt(Uri uri)
    {
        var file = Substitute.For<IStorageFile>();
        file.Path.Returns(uri);
        file.Name.Returns(Path.GetFileName(uri.AbsolutePath));
        return file;
    }

    private static DataObject FilesData(params Uri[] uris)
    {
        var data = new DataObject();
        data.Set(DataFormats.Files, uris.Select(FileAt).ToArray());
        return data;
    }

    [Fact]
    public void LibraryItems_RoundTripAsTheSameRecord()
    {
        var payload = new DragPayload.LibraryItems(LoadoutId.From(1), [LibraryItemId.From(10), LibraryItemId.From(11)]);

        DragPayloads.From(DragPayloads.ToDataObject(payload)).Should().BeSameAs(payload);
    }

    [Fact]
    public void LoadoutMods_RoundTripAsTheSameRecord()
    {
        var payload = new DragPayload.LoadoutMods(LoadoutId.From(2), [LoadoutItemId.From(20)]);

        DragPayloads.From(DragPayloads.ToDataObject(payload)).Should().BeSameAs(payload);
    }

    [Fact]
    public void DroppedFiles_RoundTripThroughOurFormat()
    {
        var payload = new DragPayload.DroppedFiles(["/tmp/a.zip"]);

        DragPayloads.From(DragPayloads.ToDataObject(payload)).Should().BeSameAs(payload);
    }

    [Fact]
    public void ToDataObject_StoresThePayloadUnderTheCustomFormat()
    {
        var payload = new DragPayload.LibraryItems(LoadoutId.From(1), [LibraryItemId.From(10)]);

        DragPayloads.ToDataObject(payload).Get(DragPayloads.Format).Should().BeSameAs(payload);
    }

    [Fact]
    public void FileItems_BecomeDroppedFilesWithLocalPaths()
    {
        var data = FilesData(new Uri("file:///tmp/one.zip"), new Uri("file:///tmp/two dir/two.7z"));

        DragPayloads.From(data).Should().BeOfType<DragPayload.DroppedFiles>()
            .Which.Paths.Should().Equal("/tmp/one.zip", "/tmp/two dir/two.7z");
    }

    [Fact]
    public void ANonLocalUri_IsLeftOut()
    {
        var data = FilesData(new Uri("file:///tmp/one.zip"), new Uri("https://example.com/two.zip"));

        DragPayloads.From(data).Should().BeOfType<DragPayload.DroppedFiles>()
            .Which.Paths.Should().Equal("/tmp/one.zip");
    }

    [Fact]
    public void OnlyNonLocalUris_GiveNoPayload()
    {
        DragPayloads.From(FilesData(new Uri("https://example.com/two.zip"))).Should().BeNull();
    }

    [Fact]
    public void OurFormat_WinsOverFiles()
    {
        var payload = new DragPayload.LibraryItems(LoadoutId.From(1), [LibraryItemId.From(10)]);
        var data = DragPayloads.ToDataObject(payload);
        data.Set(DataFormats.Files, new[] { FileAt(new Uri("file:///tmp/one.zip")) });

        DragPayloads.From(data).Should().BeSameAs(payload);
    }

    [Fact]
    public void AnEmptyDataObject_GivesNoPayload()
    {
        DragPayloads.From(new DataObject()).Should().BeNull();
    }

    [Theory]
    [InlineData(0, "0 mods")]
    [InlineData(1, "1 mod")]
    [InlineData(2, "2 mods")]
    public void ModCount_PluralisesTheNoun(int count, string expected)
    {
        ModCount.Text(count).Should().Be(expected);
    }
}
