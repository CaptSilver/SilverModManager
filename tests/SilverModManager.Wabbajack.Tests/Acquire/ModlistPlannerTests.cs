using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Library;
using NexusMods.Paths;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Tests.Install;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Wabbajack.Tests.Fixtures;

namespace SilverModManager.Wabbajack.Tests.Acquire;

public class ModlistPlannerTests(ITestOutputHelper helper) : AcquireTestBase<ModlistPlannerTests>(helper)
{
    private IModlistPlanner Planner => ServiceProvider.GetRequiredService<IModlistPlanner>();

    private async Task<LibraryFileId> AddList(ModList list)
    {
        await using var folder = ServiceProvider.GetRequiredService<TemporaryFileManager>().CreateFolder();
        var path = WabbajackFixture.Write(folder.Path, list);
        var added = await ServiceProvider.GetRequiredService<ILibraryService>().AddLocalFile(path, UnassignedAcquisition.Instance);
        return LibraryFileId.From(added.Id);
    }

    [Fact]
    public async Task APlanNamesTheModlistItWasBuiltForAndClassifiesItsArchives()
    {
        var archive = Entry(new HttpState(new Uri("https://example.test/a.7z"), []), name: "a.7z");
        var file = await AddList(ListWith(archive));

        var plan = (await Planner.BuildPlan(file, null, null, TestContext.Current.CancellationToken)).Plan;

        plan.Modlist.Should().Be(file);
        plan.Rows.Should().ContainSingle().Which.State.Should().Be(ArchiveState.Automatic);
    }

    private static ModList ListOfTwoMods(out ArchiveEntry a, out ArchiveEntry b)
    {
        var builder = new ListBuilder();
        a = Entry(new HttpState(new Uri("https://example.test/a.7z"), []), Xx64.From(1), name: "a.7z");
        b = Entry(new HttpState(new Uri("https://example.test/b.7z"), []), Xx64.From(2), name: "b.7z");
        builder.Archives.AddRange([a, b]);
        builder.Directives.AddRange(
        [
            builder.Member("mods/A/one.bin", a, "one.bin", [1]),
            builder.Member("mods/A/two.bin", a, "two.bin", [2]),
            builder.Member("mods/B/three.bin", b, "three.bin", [3]),
            builder.Inline("profiles/Default/modlist.txt", "+B\n+A\n"),
        ]);
        return builder.ToList();
    }

    [Fact]
    public async Task BuildPlanWithALoadoutNamesEachFoldersMainArchive()
    {
        var file = await AddList(ListOfTwoMods(out var a, out var b));
        var loadout = await CreateLoadout();

        var planned = await Planner.BuildPlan(file, loadout.LoadoutId, null, TestContext.Current.CancellationToken);

        planned.MainArchiveByFolder.Should().NotBeNull();
        planned.MainArchiveByFolder!["A"].Should().Be(a.XxHash64);
        planned.MainArchiveByFolder["B"].Should().Be(b.XxHash64);
        planned.Folders!.Select(f => (f.Folder, f.Total)).Should().Equal(("A", 2), ("B", 1));
    }

    [Fact]
    public async Task BuildPlanWithoutALoadoutHasNoFolderMap()
    {
        var file = await AddList(ListOfTwoMods(out _, out _));

        var planned = await Planner.BuildPlan(file, null, null, TestContext.Current.CancellationToken);

        planned.MainArchiveByFolder.Should().BeNull();
        planned.Folders.Should().BeNull();
        planned.Plan.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task AListTheInstallerRefusesStillPlansWithoutAMap()
    {
        // The loadout is a Skyrim SE one, so a Fallout 4 list is one the installer refuses.
        var file = await AddList(ListOfTwoMods(out _, out _) with { GameType = "Fallout4" });
        var loadout = await CreateLoadout();

        var planned = await Planner.BuildPlan(file, loadout.LoadoutId, null, TestContext.Current.CancellationToken);

        planned.MainArchiveByFolder.Should().BeNull();
        planned.Folders.Should().BeNull();
        planned.Plan.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task AListOfAGameThatIsNotInstalledIsRefusedInPlainWords()
    {
        var file = await AddList(ListWith() with { GameType = "Fallout4" });

        var act = () => Planner.BuildPlan(file, null, null, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<ModlistGameMissingException>()).Which.Message.Should().Contain("not installed");
    }

    [Fact]
    public async Task TheHeldArchiveCheckOfAListKnowsTheNestedArchivesItsDirectivesCopyWhole()
    {
        await using var zip = await BuildNestedZip();
        await AddWithoutNestedBytes(zip);
        var copy = new FromArchive(RelativePath.FromUnsanitizedInput("mods/A/x.bin"), Xx64.From(1), 1, new ArchiveHashPath(zip.OuterHash, [InnerZip]));
        var file = await AddList(ListWith(Entry(new HttpState(new Uri("https://example.test/a.7z"), []), zip.OuterHash, name: "outer.zip")) with { Directives = [copy] });

        var check = await Planner.ReadHeldCheck(file, TestContext.Current.CancellationToken);

        check.IsHeld(Connection.Db, ServiceProvider.GetRequiredService<ILibraryArchiveLocator>(), zip.OuterHash).Should().BeFalse();
        (await Planner.ReadHeldCheck(file, TestContext.Current.CancellationToken)).Should().BeSameAs(check, "the list is read once");
    }
}
