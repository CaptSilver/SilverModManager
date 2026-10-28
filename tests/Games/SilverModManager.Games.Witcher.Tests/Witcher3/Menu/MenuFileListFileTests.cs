using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Games.Witcher.Witcher3.Menu;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Witcher.Tests.Witcher3.Menu;

public class MenuFileListFileTests(ITestOutputHelper helper) : AIsolatedGameTest<MenuFileListFileTests, Witcher3Game>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddWitcherGames()
        .AddUniversalGameLocator<Witcher3Game>(new Version("4.0"), locationsFileSystem: IsolatedKnownFolders.Create);

    private static GamePath Menu(string name) => WitcherConstants.W3MenuConfigFolder / name;

    private static SyncNodePart Part() => new() { Hash = Hash.From(1), Size = Size.From(1), LastModifiedTicks = 0 };

    private static async Task<string> Written(bool dx12, Dictionary<GamePath, SyncNode> tree)
    {
        using var stream = new MemoryStream();
        await new MenuFileListFile(dx12).Write(stream, default, tree);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public async Task Dx12List_NamesEveryXmlInTheFolder()
    {
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [Menu("graphics.xml")] = new() { Loadout = Part() },
            [Menu("A.xml")] = new() { Disk = Part() },
            [Menu("notes.txt")] = new() { Loadout = Part() },
            [WitcherConstants.W3MenuConfigFolder.Parent / "other.xml"] = new() { Loadout = Part() },
            [Menu("gone.xml")] = new(),
        };

        (await Written(true, tree)).Should().Be("A.xml;\r\ngraphics.xml;\r\n");
    }

    [Fact]
    public async Task Dx11List_RenamesGraphics()
    {
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [Menu("graphics.xml")] = new() { Disk = Part() },
            [Menu("graphicsdx11.xml")] = new() { Disk = Part() },
            [Menu("audio.xml")] = new() { Disk = Part() },
        };

        (await Written(false, tree)).Should().Be("audio.xml;\r\ngraphicsdx11.xml;\r\n", "the DX11 list names graphicsdx11.xml once and not graphics.xml");
    }

    [Fact]
    public async Task Dx12List_LeavesTheDx11GraphicsMenuOut()
    {
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [Menu("graphics.xml")] = new() { Disk = Part() },
            [Menu("graphicsdx11.xml")] = new() { Disk = Part() },
        };

        (await Written(true, tree)).Should().Be("graphics.xml;\r\n");
    }

    [Fact]
    public async Task ListsAreWrittenOnlyWhenTheFolderExists()
    {
        var loadout = await CreateLoadout();
        var synchronizer = (Witcher3Synchronizer)Game.Synchronizer;
        var listPaths = new[] { Menu("dx12filelist.txt"), Menu("dx11filelist.txt") };

        synchronizer.IntrinsicFiles(Refresh(loadout)).Keys.Should().NotContain(listPaths);

        GameInstallation.Locations.ToAbsolutePath(WitcherConstants.W3MenuConfigFolder).CreateDirectory();

        synchronizer.IntrinsicFiles(Refresh(loadout)).Keys.Should().Contain(listPaths);
    }
}
