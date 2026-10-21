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
/// <remarks>
/// Not covered: unmanaging. The synchronizer has no rule that backs up an intrinsic file that was already on
/// disk, so resetting the game deletes the user's openmw.cfg instead of restoring it (a pre-existing
/// Plugins.txt is treated the same way). Restoring it byte for byte needs a change to the synchronizer rules.
/// </remarks>
public class MorrowindOpenMwRoundTripTests(ITestOutputHelper helper) : AOpenMwTest<MorrowindOpenMwRoundTripTests>(helper)
{
    protected override int[] RootsWithConfig => [0];

    private AbsolutePath Config => Roots.Candidates[0] / "openmw.cfg";

    private async Task<string[]> ContentLines() =>
        (await Config.ReadAllTextAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("content=")).ToArray();

    private async Task Apply(Loadout.ReadOnly loadout, Func<string, bool> done)
    {
        await Synchronizer.Synchronize(Refresh(loadout));

        // A file already on disk at an intrinsic path is ingested by the first Synchronize and only written by
        // the next one, so a missing block after one pass is not yet a failure.
        if (!done(await Config.ReadAllTextAsync()))
            await Synchronizer.Synchronize(Refresh(loadout));
    }

    [Fact]
    public async Task OnlyTheOwnedContentLinesChangeOnApply()
    {
        var loadout = await CreateLoadout();
        var deployedMod = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "Data Files/Mod.esp"));

        var archive = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { { "Data Files/Mod.esp", "mod"u8.ToArray() } });
        var library = await RegisterLocalArchive(archive.Path);
        var installed = await LoadoutManager.InstallItem(library.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId);
        var group = installed.LoadoutItemGroup!.Value;

        await Apply(loadout, text => text.Contains("content=Mod.esp"));

        deployedMod.FileExists.Should().BeTrue();
        (await ContentLines()).Should().Equal("content=builtin.omwscripts", "content=Morrowind.esm", "content=Mod.esp");
        (await Config.ReadAllTextAsync()).Should().StartWith("data=\"/games/Morrowind/Data Files\"\n");

        await LoadoutManager.RemoveItems([group.LoadoutItemGroupId]);
        await Apply(loadout, text => !text.Contains("content=Mod.esp"));

        deployedMod.FileExists.Should().BeFalse();
        (await ContentLines()).Should().Equal("content=builtin.omwscripts", "content=Morrowind.esm");
        (await Config.ReadAllTextAsync()).Should().StartWith("data=\"/games/Morrowind/Data Files\"\n");

        // A removal leaves the file rewritten outside a write, so the next add must still reach it.
        var secondArchive = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { { "Data Files/Second.esp", "second"u8.ToArray() } });
        var secondLibrary = await RegisterLocalArchive(secondArchive.Path);
        await LoadoutManager.InstallItem(secondLibrary.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId);
        await Apply(loadout, text => text.Contains("content=Second.esp"));

        (await ContentLines()).Should().Equal("content=builtin.omwscripts", "content=Morrowind.esm", "content=Second.esp");
        (await Config.ReadAllTextAsync()).Should().StartWith("data=\"/games/Morrowind/Data Files\"\n");
    }
}
