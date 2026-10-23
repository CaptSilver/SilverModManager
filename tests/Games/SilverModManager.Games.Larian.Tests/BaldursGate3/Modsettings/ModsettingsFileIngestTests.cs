using FluentAssertions;
using NexusMods.Abstractions.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.Larian.Tests.BaldursGate3.Modsettings;

/// <summary>
/// What the game does to <c>modsettings.lsx</c> between two Applies. Its edits are never adopted: the next Apply
/// writes the app's list back and leaves a notice saying what was replaced.
/// </summary>
public class ModsettingsFileIngestTests(ITestOutputHelper helper) : ModsettingsFixture(helper)
{
    private async Task<Loadout.ReadOnly> AppliedTwoPaks()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());
        await InstallMod(loadout, "Beta mod", "beta.pak", BetaPak());
        await WriteModsettings(BaseEntries);
        await Apply(loadout);
        return loadout;
    }

    private string? ReplacedSummary(Loadout.ReadOnly loadout)
    {
        string? summary = null;
        using var _ = ((IReplacedGameEditSource)Variety).ReplacedEditSummary(SortOrderOf(loadout)).Subscribe(s => summary = s);
        return summary;
    }

    [Fact]
    public async Task ALeftAloneFileLeavesNoNotice()
    {
        var loadout = await AppliedTwoPaks();

        await Apply(loadout);

        ReplacedSummary(loadout).Should().BeNull();
    }

    [Fact]
    public async Task AReorderedFileIsReplacedWithANotice()
    {
        var loadout = await AppliedTwoPaks();
        await WriteModsettings([.. BaseEntries, Entry("Beta", "Beta", "0", BetaUuid), Entry("Alpha", "Alpha", "0", AlphaUuid)]);

        await Apply(loadout);

        DiskNames().Should().Equal("GustavX", "HonourX", "Alpha", "Beta");
        ReplacedSummary(loadout).Should().NotBeNullOrEmpty().And.Contain("order");
    }

    [Fact]
    public async Task APakTheGameSwitchedOffIsBroughtBackWithANotice()
    {
        var loadout = await AppliedTwoPaks();
        await WriteModsettings([.. BaseEntries, Entry("Alpha", "Alpha", "0", AlphaUuid)]);

        await Apply(loadout);

        DiskNames().Should().Equal("GustavX", "HonourX", "Alpha", "Beta");
        ReplacedSummary(loadout).Should().NotBeNullOrEmpty().And.Contain("beta.pak");
    }

    [Fact]
    public async Task TheNoticeEndsWithTheNextApplyThatFindsTheFileUntouched()
    {
        var loadout = await AppliedTwoPaks();
        await WriteModsettings([.. BaseEntries, Entry("Alpha", "Alpha", "0", AlphaUuid)]);
        await Apply(loadout);
        ReplacedSummary(loadout).Should().NotBeNullOrEmpty();

        await Apply(loadout);

        ReplacedSummary(loadout).Should().BeNull();
    }

    [Fact]
    public async Task ChangesToBaseEntriesAndUnknownModulesMakeNoNotice()
    {
        var loadout = await AppliedTwoPaks();
        await WriteModsettings([
            Entry("HonourX", "HonourX", "0", HonourUuid),
            Entry("GustavX", "GustavX", "0", GustavUuid),
            Entry("Stranger", "Stranger", "0", "e5e5e5e5-0000-4000-8000-000000000005"),
            Entry("Alpha", "Alpha", "0", AlphaUuid),
            Entry("Beta", "Beta", "0", BetaUuid),
        ]);

        await Apply(loadout);

        ReplacedSummary(loadout).Should().BeNull();
    }
}
