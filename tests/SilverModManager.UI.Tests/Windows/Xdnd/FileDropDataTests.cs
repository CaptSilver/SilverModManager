using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using FluentAssertions;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>Dropped paths as the IStorageItems the window's drop handlers read through TryGetLocalPath.</summary>
public class FileDropDataTests(IServiceProvider provider) : AUiTest(provider)
{
    private readonly string _root = TestScratch.Combine("file-drop-data", Guid.NewGuid().ToString("N"));

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string MakeFile(string name)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private string MakeFolder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private async Task<FileDrop> CreateAsync(params string[] paths)
    {
        FileDrop? drop = null;
        await OnUi(async () =>
        {
            var window = Track(new Window());
            ShowRendered(window);
            drop = await new FileDropData(window.StorageProvider).CreateAsync(paths);
        });
        return drop!;
    }

    private static IReadOnlyList<IStorageItem> Items(FileDrop drop) =>
        drop.Data.Get(DataFormats.Files).Should().BeAssignableTo<IEnumerable<IStorageItem>>().Subject.ToList();

    [Theory]
    [InlineData("plain.zip")]
    [InlineData("with space.zip")]
    [InlineData("hash#tag.zip")]
    [InlineData("percent%20literal.zip")]
    [InlineData("100%.zip")]
    [InlineData("café.zip")]
    [InlineData("question?mark.zip")]
    public async Task TheLocalPathComesBackUnchangedForAwkwardFileNames(string name)
    {
        var path = MakeFile(name);

        var drop = await CreateAsync(path);

        Items(drop).Should().ContainSingle().Which.TryGetLocalPath().Should().Be(path);
    }

    [Fact]
    public async Task AFolderComesBackAsAFolderWithItsLocalPath()
    {
        var path = MakeFolder("a folder#1");

        var drop = await CreateAsync(path);

        var item = Items(drop).Should().ContainSingle().Subject;
        item.Should().BeAssignableTo<IStorageFolder>();
        item.TryGetLocalPath().Should().Be(path);
    }

    [Fact]
    public async Task AFileComesBackAsAFile()
    {
        var drop = await CreateAsync(MakeFile("a.zip"));

        Items(drop).Should().ContainSingle().Which.Should().BeAssignableTo<IStorageFile>();
    }

    [Fact]
    public async Task ItemsKeepTheOrderOfThePaths()
    {
        var b = MakeFile("b.zip");
        var a = MakeFile("a.zip");
        var folder = MakeFolder("c");

        var drop = await CreateAsync(b, folder, a);

        Items(drop).Select(i => i.TryGetLocalPath()).Should().Equal(b, folder, a);
    }

    [Fact]
    public async Task APathThatDoesNotExistIsReportedAndLeftOut()
    {
        var present = MakeFile("here.zip");
        var gone = Path.Combine(_root, "gone.zip");

        var drop = await CreateAsync(gone, present);

        Items(drop).Select(i => i.TryGetLocalPath()).Should().Equal(present);
        drop.Missing.Should().Equal(gone);
    }

    [Fact]
    public async Task NoPathsGiveAnEmptyFileList()
    {
        var drop = await CreateAsync();

        drop.Data.Contains(DataFormats.Files).Should().BeTrue();
        Items(drop).Should().BeEmpty();
        drop.Missing.Should().BeEmpty();
    }
}
