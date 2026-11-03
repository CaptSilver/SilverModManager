using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.App.UI.Pages.Diff.ApplyDiff;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.UI.Tests;

/// <summary>The wording of the confirm dialog for removing files from the loadout.</summary>
public class FileRemovalTextTests
{
    private static long _next = 1;

    private static FileRemovalRow Row(string path, string group = "Mod", string? collection = null, string? lockReason = null,
        string? blocker = null, bool disabled = false, bool external = false, bool winner = true) =>
        new(EntityId.From((ulong)Interlocked.Increment(ref _next)), new GamePath(LocationId.Game, path), group,
            LoadoutItemGroupId.From(EntityId.From((ulong)Math.Abs(group.GetHashCode()) + 1)), collection, lockReason, blocker,
            disabled, external, winner);

    private static string Text(params FileRemovalRow[] rows) =>
        FileRemovalText.For(new FileRemovalPlan(new FileRemovalRequest(default, []), rows));

    [Fact]
    public void EachRowIsOneLineWithGroupAndCollection()
    {
        var text = Text(Row("Data/a.dll", "Upscaler Base Plugin", collection: "FUS"), Row("Data/b.dll", "Other"));

        text.Should().StartWith("These files leave the loadout:\n\n");
        text.Should().Contain("Data/a.dll — Upscaler Base Plugin, in FUS\n");
        text.Should().Contain("Data/b.dll — Other\n");
        text.Should().Contain("The next Apply deletes the ones that are in the game folder, or puts the game's own version back");
    }

    [Fact]
    public void ADisabledRowSaysSwitchedOff()
    {
        Text(Row("Data/a.dll", disabled: true)).Should().Contain("Data/a.dll — Mod (switched off)");
    }

    [Fact]
    public void ALosingRowSaysItIsNotWhatTheGameGets()
    {
        Text(Row("Data/a.dll", winner: false)).Should().Contain("Data/a.dll — Mod (not the file the game would get)");
    }

    [Fact]
    public void OverridesIsCalledExternalChanges()
    {
        Text(Row("Data/a.ini", "Overrides", external: true)).Should().Contain("Data/a.ini — External Changes");
    }

    [Fact]
    public void MoreThanTwentyRowsAreSummarisedPerGroup()
    {
        var rows = Enumerable.Range(0, 12).Select(i => Row($"Data/big/{i}.dll", "Big"))
            .Concat(Enumerable.Range(0, 8).Select(i => Row($"Data/mid/{i}.dll", "Mid", collection: "List")))
            .Concat(Enumerable.Range(0, 5).Select(i => Row($"Data/small/{i}.dll", "Small")))
            .ToArray();

        var text = Text(rows);

        text.Should().Contain("Big — 12 files\nMid, in List — 8 files\nSmall — 5 files");
        text.Should().NotContain("Data/big/0.dll");
    }

    [Fact]
    public void ALockedCollectionGetsOneParagraphWithItsReasonAndTheResumeWarning()
    {
        const string reason = "FUS is a Wabbajack modlist; it decides what it contains";
        var text = Text(Row("Data/a.dll", collection: "FUS", lockReason: reason), Row("Data/b.dll", collection: "FUS", lockReason: reason));

        text.Should().Contain(reason + ". Removing files from its mods is a local edit: reinstalling or resuming the list puts them back.");
        text.Split(reason).Length.Should().Be(2, "the paragraph appears once per collection");
    }

    [Fact]
    public void ExternalChangesRowsGetTheirOwnParagraphAboutDiskAndRevert()
    {
        var text = Text(Row("Data/foo.ini", "Overrides", external: true), Row("Data/bar.ini", "Overrides", external: true));

        text.Should().Contain("Data/foo.ini and Data/bar.ini are files you put in the game folder yourself.");
        text.Should().Contain("`loadout revert` brings them back");
    }

    [Fact]
    public void BlockedRowsAreNamedAsStaying()
    {
        var text = Text(Row("Data/a.dll"), Row("Data/Creations/Foo.esm", blocker: "Data/Creations/Foo.esm was installed by the game and stays."));

        text.Should().Contain("Data/Creations/Foo.esm was installed by the game and stays.");
        text.Should().NotContain("Data/Creations/Foo.esm — ", "a blocked row is not on the list of files that leave");
    }
}
