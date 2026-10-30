using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Library;
using NexusMods.Paths;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Tests.Fixtures;

namespace SilverModManager.Wabbajack.Tests.Acquire;

public class ModlistPlannerTests(ITestOutputHelper helper) : AcquireTestBase<ModlistPlannerTests>(helper)
{
    private IModlistPlanner Planner => ServiceProvider.GetRequiredService<IModlistPlanner>();

    private async Task<LibraryFileId> AddList(ModList list)
    {
        await using var folder = ServiceProvider.GetRequiredService<TemporaryFileManager>().CreateFolder();
        var path = WabbajackFixture.Write(folder.Path, list);
        var added = await ServiceProvider.GetRequiredService<ILibraryService>().AddLocalFile(path);
        return LibraryFileId.From(added.Id);
    }

    [Fact]
    public async Task APlanNamesTheModlistItWasBuiltForAndClassifiesItsArchives()
    {
        var archive = Entry(new HttpState(new Uri("https://example.test/a.7z"), []), name: "a.7z");
        var file = await AddList(ListWith(archive));

        var plan = await Planner.BuildPlan(file, null, null, TestContext.Current.CancellationToken);

        plan.Modlist.Should().Be(file);
        plan.Rows.Should().ContainSingle().Which.State.Should().Be(ArchiveState.Automatic);
    }

    [Fact]
    public async Task AListOfAGameThatIsNotInstalledIsRefusedInPlainWords()
    {
        var file = await AddList(ListWith() with { GameType = "Fallout4" });

        var act = () => Planner.BuildPlan(file, null, null, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<ModlistGameMissingException>()).Which.Message.Should().Contain("not installed");
    }
}
