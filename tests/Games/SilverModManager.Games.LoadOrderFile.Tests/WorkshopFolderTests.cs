using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.LoadOrderFile.Tests;

public class WorkshopFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "workshop-folder-" + Guid.NewGuid().ToString("N"));

    private static AbsolutePath Absolute(string path) => FileSystem.Shared.FromUnsanitizedFullPath(path);

    private string Game => Path.Combine(_root, "steamapps", "common", "XCOM 2");

    private void Subscribe(string name)
    {
        Directory.CreateDirectory(Path.Combine(_root, "steamapps", "workshop", "content", "268500", name));
    }

    [Fact]
    public void AFolderWhoseNameIsNotANumberIsNotAnItem()
    {
        Directory.CreateDirectory(Game);
        Subscribe("200");
        Subscribe("notes");
        Subscribe("10x");

        WorkshopFolder.Items(Absolute(Game), 268500).Select(item => item.PublishedFileId).Should().Equal(200UL);
    }

    [Fact]
    public void ItemsComeInAscendingOrderOfFolderName()
    {
        Directory.CreateDirectory(Game);
        Subscribe("200");
        Subscribe("100");

        var items = WorkshopFolder.Items(Absolute(Game), 268500);

        items.Select(item => item.PublishedFileId).Should().Equal(100UL, 200UL);
        items[0].Folder.FileName.Should().Be("100");
    }

    [Fact]
    public void AGameOutsideASteamLibraryHasNoItems()
    {
        var game = Path.Combine(_root, "games", "xcom2");
        Directory.CreateDirectory(game);

        WorkshopFolder.Items(Absolute(game), 268500).Should().BeEmpty();
    }

    [Fact]
    public void TheContentFolderIsFoundFromTheGameFolderAndIsNoneUntilItExists()
    {
        Directory.CreateDirectory(Game);
        WorkshopFolder.Content(Absolute(Game), 268500).Should().BeNull();

        Subscribe("100");

        WorkshopFolder.Content(Absolute(Game), 268500)!.Value.GetFullPath().Should().EndWith(Path.Combine("steamapps", "workshop", "content", "268500"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
