using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.Morrowind.OpenMw;

/// <summary>
/// Applies a mod with a plugin to Morrowind with an existing openmw.cfg and takes it off again. Only the
/// content= lines for files under Data Files change; builtin.omwscripts, data= lines and the vanilla master stay.
/// </summary>
public class MorrowindOpenMwRoundTripTests(ITestOutputHelper helper) : AOpenMwTest<MorrowindOpenMwRoundTripTests>(helper)
{
    protected override int[] RootsWithConfig => [0];

    private AbsolutePath Config => Roots.Candidates[0] / "openmw.cfg";

    private async Task<string[]> ContentLines() =>
        (await Config.ReadAllTextAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("content=")).ToArray();

    private async Task Apply(Loadout.ReadOnly loadout) => await Synchronizer.Synchronize(Refresh(loadout));

    [Fact]
    public async Task OnlyTheOwnedContentLinesChangeOnApply()
    {
        var loadout = await CreateLoadout();
        var deployedMod = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "Data Files/Mod.esp"));

        var archive = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { { "Data Files/Mod.esp", "mod"u8.ToArray() } });
        var library = await RegisterLocalArchive(archive.Path);
        var installed = await LoadoutManager.InstallItem(library.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId);
        var group = installed.LoadoutItemGroup!.Value;

        await Apply(loadout);

        deployedMod.FileExists.Should().BeTrue();
        (await ContentLines()).Should().Equal("content=builtin.omwscripts", "content=Morrowind.esm", "content=Mod.esp");
        (await Config.ReadAllTextAsync()).Should().StartWith("data=\"/games/Morrowind/Data Files\"\n");

        await LoadoutManager.RemoveItems([group.LoadoutItemGroupId]);
        await Apply(loadout);

        deployedMod.FileExists.Should().BeFalse();
        (await ContentLines()).Should().Equal("content=builtin.omwscripts", "content=Morrowind.esm");
        (await Config.ReadAllTextAsync()).Should().StartWith("data=\"/games/Morrowind/Data Files\"\n");

        // A removal leaves the file rewritten outside a write, so the next add must still reach it.
        var secondArchive = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { { "Data Files/Second.esp", "second"u8.ToArray() } });
        var secondLibrary = await RegisterLocalArchive(secondArchive.Path);
        await LoadoutManager.InstallItem(secondLibrary.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId);
        await Apply(loadout);

        (await ContentLines()).Should().Equal("content=builtin.omwscripts", "content=Morrowind.esm", "content=Second.esp");
        (await Config.ReadAllTextAsync()).Should().StartWith("data=\"/games/Morrowind/Data Files\"\n");
    }

    [Fact]
    public async Task UnmanagingPutsTheUsersConfigBackByteForByte()
    {
        var original = await Config.ReadAllBytesAsync();
        var loadout = await CreateLoadout();
        var archive = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { { "Data Files/Mod.esp", "mod"u8.ToArray() } });
        var library = await RegisterLocalArchive(archive.Path);
        await LoadoutManager.InstallItem(library.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId);
        await Apply(loadout);
        (await Config.ReadAllTextAsync()).Should().Contain("content=Mod.esp");

        await LoadoutManager.UnManage(GameInstallation);

        Config.FileExists.Should().BeTrue("the user had that file before the game was managed");
        (await Config.ReadAllBytesAsync()).Should().Equal(original);
    }
}
