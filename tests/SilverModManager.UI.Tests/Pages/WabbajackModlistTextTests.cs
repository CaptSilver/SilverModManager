using FluentAssertions;
using SilverModManager.App.UI.Pages.Wabbajack;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Hashes;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Install;

namespace SilverModManager.UI.Tests.Pages;

public class WabbajackModlistTextTests
{
    private static ModlistInstallProgress.Snapshot Progress(InstallPhase phase, int done = 0, int total = 0)
        => ModlistInstallProgress.Snapshot.Empty with { Phase = phase, DoneItems = done, TotalItems = total };

    [Fact]
    public void Progress_NamesEachOfTheFourPhases()
    {
        WabbajackModlistText.Progress(Progress(InstallPhase.Check)).Should().Be("Checking the list");
        WabbajackModlistText.Progress(Progress(InstallPhase.Resolve, 3, 40)).Should().Be("Locating files in archives: 3 of 40");
        WabbajackModlistText.Progress(Progress(InstallPhase.Write, 12, 40)).Should().Be("Writing files: 12 of 40");
        WabbajackModlistText.Progress(Progress(InstallPhase.Order)).Should().Be("Ordering mods and plugins");
    }

    [Fact]
    public void Fraction_IsTheShareOfTheCurrentPhaseAndZeroWhenThePhaseHasNoItems()
    {
        WabbajackModlistText.Fraction(Progress(InstallPhase.Write, 10, 40)).Should().BeApproximately(0.25, 1e-9);
        WabbajackModlistText.Fraction(Progress(InstallPhase.Order)).Should().Be(0);
    }

    [Fact]
    public void FreeSpace_IsAPlainNoteWhenThereIsRoom()
    {
        var note = WabbajackModlistText.FreeSpace(Size.GB * 10, isLowerBound: false, Size.GB * 50);

        note.IsWarning.Should().BeFalse();
        note.Text.Should().Contain("Extracted size").And.NotContain("at least").And.Contain("free");
    }

    [Fact]
    public void FreeSpace_WarnsWhenTheEstimateIsMoreThanTheFreeSpace()
    {
        var note = WabbajackModlistText.FreeSpace(Size.GB * 80, isLowerBound: false, Size.GB * 50);

        note.IsWarning.Should().BeTrue();
        note.Text.Should().Contain("Not enough free space");
    }

    [Fact]
    public void FreeSpace_SaysAtLeastWhileAnArchiveIsMissing()
    {
        WabbajackModlistText.FreeSpace(Size.GB * 10, isLowerBound: true, Size.GB * 50).Text.Should().Contain("at least");
        WabbajackModlistText.FreeSpace(Size.GB * 80, isLowerBound: true, Size.GB * 50).Text.Should().Contain("at least");
    }

    [Fact]
    public void FreeSpace_WithoutAProbeResultStillShowsTheEstimate()
    {
        var note = WabbajackModlistText.FreeSpace(Size.GB * 10, isLowerBound: false, free: null);

        note.IsWarning.Should().BeFalse();
        note.Text.Should().Contain("Extracted size");
    }

    [Fact]
    public void LoadoutsFor_KeepsOnlyTheLoadoutsOfTheListsGame()
    {
        var skyrim = GameId.From("CreationEngine.SkyrimSE");
        var fallout = GameId.From("CreationEngine.Fallout4");
        var a = LoadoutId.From(SilverModManager.MnemonicDB.Abstractions.EntityId.From(1));
        var b = LoadoutId.From(SilverModManager.MnemonicDB.Abstractions.EntityId.From(2));
        var c = LoadoutId.From(SilverModManager.MnemonicDB.Abstractions.EntityId.From(3));

        var chosen = WabbajackModlistText.LoadoutsFor([(a, skyrim), (b, fallout), (c, skyrim)], skyrim);

        chosen.Should().Equal(a, c);
    }

    private static ArchivePlanRow Row(ArchiveState state, string name = "a.7z")
        => new(new ArchiveEntry(FileIdentity.XxHash64(Hash.From((ulong)name.GetHashCode())), 1_000, name, "", new ManualState(new Uri("https://example.com/a"), "get it")), state, null, null, null);

    private static ArchivePlan Plan(params ArchivePlanRow[] rows) => new(rows);

    [Fact]
    public void InstallBlockedReason_IsEmptyWhenInstallIsAllowed()
    {
        WabbajackModlistText.InstallBlockedReason(true, false, true, false, Plan(Row(ArchiveState.Have)), false).Should().BeEmpty();
        WabbajackModlistText.InstallBlockedReason(true, false, true, false, Plan(Row(ArchiveState.Manual)), true).Should().BeEmpty();
    }

    [Fact]
    public void InstallBlockedReason_NamesARunningInstallBeforeAnythingElse()
        => WabbajackModlistText.InstallBlockedReason(false, true, false, true, Plan(Row(ArchiveState.Manual)), false)
            .Should().Be("An install is running. Its progress is shown below.");

    [Fact]
    public void InstallBlockedReason_SaysTheListIsBeingChecked()
        => WabbajackModlistText.InstallBlockedReason(true, true, false, false, Plan(), false)
            .Should().StartWith("Checking the list against your library and game files");

    [Fact]
    public void InstallBlockedReason_SaysWhenTheCheckDidNotFinish()
        => WabbajackModlistText.InstallBlockedReason(true, false, false, false, Plan(), false)
            .Should().Contain("could not be checked").And.Contain("message above");

    [Fact]
    public void InstallBlockedReason_AsksForALoadoutWhenNoneIsPicked()
        => WabbajackModlistText.InstallBlockedReason(false, false, true, false, Plan(Row(ArchiveState.Have)), false)
            .Should().Be("Choose a loadout to install into.");

    [Fact]
    public void InstallBlockedReason_CountsMissingArchivesAndGameFilesOfAnotherVersion()
    {
        var plan = Plan(Row(ArchiveState.Manual, "a"), Row(ArchiveState.GameFileVersion, "b"), Row(ArchiveState.GameFileVersion, "c"), Row(ArchiveState.Have, "d"));

        var reason = WabbajackModlistText.InstallBlockedReason(true, false, true, false, plan, false);

        reason.Should().Contain("1 of 4 archives is not in your library yet")
            .And.Contain("2 game files differ from the version the list was made for")
            .And.Contain("Skip files from missing archives");
    }

    [Fact]
    public void InstallBlockedReason_SaysOneMissingArchiveInTheSingular()
        => WabbajackModlistText.InstallBlockedReason(true, false, true, false, Plan(Row(ArchiveState.Manual), Row(ArchiveState.Have, "b")), false)
            .Should().Contain("1 of 2 archives is not in your library yet");

    [Fact]
    public void DownloadBlockedReason_IsEmptyWhenAnArchiveCanBeFetched()
        => WabbajackModlistText.DownloadBlockedReason(false, false, true, false, Plan(Row(ArchiveState.Browser))).Should().BeEmpty();

    [Fact]
    public void DownloadBlockedReason_CoversRunningCheckingFailedEmptyDoneAndManual()
    {
        WabbajackModlistText.DownloadBlockedReason(false, false, true, true, Plan(Row(ArchiveState.Browser))).Should().Be("Downloads are running. Their progress is shown above.");
        WabbajackModlistText.DownloadBlockedReason(true, false, false, false, Plan()).Should().StartWith("Checking the list");
        WabbajackModlistText.DownloadBlockedReason(false, false, false, false, Plan()).Should().Contain("could not be checked");
        WabbajackModlistText.DownloadBlockedReason(false, false, true, false, Plan()).Should().Be("The list names no archives.");
        WabbajackModlistText.DownloadBlockedReason(false, false, true, false, Plan(Row(ArchiveState.Have))).Should().StartWith("Every archive is already in your library");
        WabbajackModlistText.DownloadBlockedReason(false, false, true, false, Plan(Row(ArchiveState.Manual), Row(ArchiveState.Unsupported, "b")))
            .Should().StartWith("None of the 2 missing archives can be fetched by the app").And.Contain("Open page");
        WabbajackModlistText.DownloadBlockedReason(false, false, true, false, Plan(Row(ArchiveState.Manual), Row(ArchiveState.GameFileVersion, "b")))
            .Should().StartWith("The missing archive cannot be fetched by the app").And.Contain("1 game file differs");
        WabbajackModlistText.DownloadBlockedReason(false, false, true, false, Plan(Row(ArchiveState.GameFileVersion, "b")))
            .Should().StartWith("1 game file differs from the version the list was made for").And.Contain("downloading cannot change that");
    }

    [Fact]
    public void DownloadBlockedReason_SaysTheListIsBeingCheckedWhilePreparingTheDownloads()
        => WabbajackModlistText.DownloadBlockedReason(false, true, true, false, Plan(Row(ArchiveState.Automatic))).Should().StartWith("Checking the list");

    [Fact]
    public void InstallBlockedReason_OnlyTellsYouToGetArchivesWhenSomeAreMissing()
    {
        var onlyDiffering = WabbajackModlistText.InstallBlockedReason(true, false, true, false, Plan(Row(ArchiveState.GameFileVersion), Row(ArchiveState.Have, "b")), false);
        onlyDiffering.Should().NotContain("Get what is missing").And.Contain("Skip files from missing archives");

        var missing = WabbajackModlistText.InstallBlockedReason(true, false, true, false, Plan(Row(ArchiveState.Manual), Row(ArchiveState.GameFileVersion, "b")), false);
        missing.Should().Contain("Get what is missing");
    }

    [Fact]
    public void OpenNextFiveBlockedReason_SaysToPressDownloadFirst_OnlyWhenDownloadCanBePressed()
    {
        var browser = Plan(Row(ArchiveState.Browser));
        WabbajackModlistText.OpenNextFiveBlockedReason(false, "", browser).Should().Contain("Press Download first");
        WabbajackModlistText.OpenNextFiveBlockedReason(true, "", browser).Should().BeEmpty();
    }

    [Fact]
    public void OpenNextFiveBlockedReason_RepeatsDownloadsReasonWhenDownloadIsOff()
        => WabbajackModlistText.OpenNextFiveBlockedReason(false, "Every archive is already in your library.", Plan(Row(ArchiveState.Have)))
            .Should().Be("Every archive is already in your library.");

    [Fact]
    public void OpenNextFiveBlockedReason_SaysWhenNoArchiveNeedsTheBrowser()
        => WabbajackModlistText.OpenNextFiveBlockedReason(false, "", Plan(Row(ArchiveState.Automatic))).Should().Be("No archive needs your browser.");
}
