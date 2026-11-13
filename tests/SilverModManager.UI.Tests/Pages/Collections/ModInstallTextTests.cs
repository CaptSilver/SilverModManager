using FluentAssertions;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.Sdk.Loadouts.Install;

namespace SilverModManager.UI.Tests.Pages.Collections;

public class ModInstallTextTests
{
    private static ModInstallRowState Row(ModInstallState state, int resolved = 0, int written = 0, int total = 10, string? reason = null, string? detail = null)
        => new(state, resolved, written, total, reason, detail);

    [Theory]
    [InlineData(ModInstallState.Waiting, "waiting")]
    [InlineData(ModInstallState.Downloading, "downloading")]
    [InlineData(ModInstallState.NeedsUser, "get it yourself")]
    [InlineData(ModInstallState.Extracting, "extracting")]
    [InlineData(ModInstallState.Installing, "installing")]
    [InlineData(ModInstallState.Installed, "installed")]
    [InlineData(ModInstallState.Failed, "failed")]
    public void StateStringsAreTheApprovedWords(ModInstallState state, string text)
        => ModInstallText.State(state).Should().Be(text);

    [Fact]
    public void CountIsDoneOfTotalFiles()
    {
        ModInstallText.Count(Row(ModInstallState.Installing, written: 412, total: 1308)).Should().Be("412 of 1,308 files");
        ModInstallText.Count(Row(ModInstallState.Extracting, resolved: 7, written: 0, total: 20)).Should().Be("7 of 20 files");
    }

    [Fact]
    public void HeaderAppendsFailedOnlyWhenAny()
    {
        ModInstallText.Header(new ModInstallTotals(3112, 2947, 4)).Should().Be("2,947 of 3,112 mods installed · 4 failed");
        ModInstallText.Header(new ModInstallTotals(3112, 2947, 0)).Should().Be("2,947 of 3,112 mods installed");
    }

    [Fact]
    public void TailIsTheReasonForAFailedRowElseTheDetail()
    {
        ModInstallText.Tail(Row(ModInstallState.Failed, reason: "archive missing", detail: " · ignored")).Should().Be("archive missing");
        ModInstallText.Tail(Row(ModInstallState.Installing, detail: " · waiting for the merge pass")).Should().Be(" · waiting for the merge pass");
        ModInstallText.Tail(Row(ModInstallState.Installing)).Should().Be("");
    }

    [Fact]
    public void ASummaryIsTheCountAndDetailOrTheReasonAlone()
    {
        ModInstallText.Summary(Row(ModInstallState.Installing, written: 3, total: 10, detail: " · building a.bsa (1 of 2)"))
            .Should().Be("3 of 10 files · building a.bsa (1 of 2)");
        ModInstallText.Summary(Row(ModInstallState.Failed, written: 3, total: 10, reason: "archive missing")).Should().Be("archive missing");
    }

    [Fact]
    public void NotInstalledMatchesEverythingButInstalled()
    {
        foreach (var state in Enum.GetValues<ModInstallState>())
            ModInstallText.Matches(ModInstallFilter.NotInstalled, Row(state)).Should().Be(state != ModInstallState.Installed, state.ToString());
    }

    [Fact]
    public void FailedMatchesOnlyFailed()
    {
        foreach (var state in Enum.GetValues<ModInstallState>())
        {
            ModInstallText.Matches(ModInstallFilter.Failed, Row(state)).Should().Be(state == ModInstallState.Failed, state.ToString());
            ModInstallText.Matches(ModInstallFilter.All, Row(state)).Should().BeTrue();
        }
    }
}
