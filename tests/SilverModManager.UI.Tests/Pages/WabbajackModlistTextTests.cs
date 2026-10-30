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
            .Should().Be("An install is running.");

    [Fact]
    public void InstallBlockedReason_SaysTheListIsBeingChecked()
        => WabbajackModlistText.InstallBlockedReason(true, true, false, false, Plan(), false)
            .Should().StartWith("Checking the list against your library and game files");

    [Fact]
    public void InstallBlockedReason_SaysWhenTheCheckDidNotFinish()
        => WabbajackModlistText.InstallBlockedReason(true, false, false, false, Plan(), false)
            .Should().Be("The list could not be checked.");

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
        WabbajackModlistText.DownloadBlockedReason(false, false, true, true, Plan(Row(ArchiveState.Browser))).Should().Be("Downloads are running.");
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

    [Fact]
    public void Checking_NamesEachPhaseAndItsCount()
    {
        WabbajackModlistText.Checking(new PlanProgress(PlanPhase.ReadingList)).Should().Be("Reading the list");
        WabbajackModlistText.Checking(new PlanProgress(PlanPhase.CheckingLibrary, 412, 1294)).Should().Be("Checking archives against your library: 412 of 1294");
        WabbajackModlistText.Checking(new PlanProgress(PlanPhase.CheckingGameFolder, 7, 90)).Should().Be("Looking for the list's files in your game folder: 7 of 90");
    }

    [Fact]
    public void Checking_CountsGameFileBytesInGigabytesOnceTheyAreBig_AndNamesTheFile()
    {
        WabbajackModlistText.Checking(new PlanProgress(PlanPhase.HashingGameFiles, 2_100_000_000, 8_400_000_000, "Textures0.bsa"))
            .Should().Be("Hashing game files: 2.1 of 8.4 GB (Textures0.bsa)");
        WabbajackModlistText.Checking(new PlanProgress(PlanPhase.HashingGameFiles, 12_000_000, 480_000_000))
            .Should().Be("Hashing game files: 12.0 of 480.0 MB");
    }

    [Fact]
    public void Checking_WithNothingToCountDropsTheCount()
        => WabbajackModlistText.Checking(new PlanProgress(PlanPhase.CheckingLibrary, 0, 0)).Should().Be("Checking archives against your library");

    [Fact]
    public void CheckFraction_IsTheShareDone_AndAPhaseWithoutATotalIsIndeterminate()
    {
        WabbajackModlistText.CheckFraction(new PlanProgress(PlanPhase.CheckingLibrary, 250, 1000)).Should().BeApproximately(0.25, 1e-9);
        WabbajackModlistText.CheckFraction(new PlanProgress(PlanPhase.ReadingList)).Should().Be(0);
        WabbajackModlistText.IsIndeterminate(new PlanProgress(PlanPhase.ReadingList)).Should().BeTrue();
        WabbajackModlistText.IsIndeterminate(new PlanProgress(PlanPhase.CheckingLibrary, 1, 1000)).Should().BeFalse();
    }

    [Fact]
    public void Counts_SortEveryStateIntoReadyToGetByHandOrDiffering()
    {
        var counts = WabbajackModlistText.Counts(Plan(
            Row(ArchiveState.Have), Row(ArchiveState.GameFile),
            Row(ArchiveState.Automatic), Row(ArchiveState.Browser), Row(ArchiveState.Browser),
            Row(ArchiveState.Manual), Row(ArchiveState.Unsupported),
            Row(ArchiveState.GameFileVersion)));

        counts.Should().Be(new WabbajackPlanCounts(Ready: 2, ToGet: 3, ByHand: 2, GameFilesDiffer: 1));
    }

    private const string Checking = "Checking the list against your library and game files. This can take a while for a big list.";

    private static WabbajackStatus Status(
        string error = "", string install = "", double installFraction = 0,
        string check = "", double checkFraction = 0, bool checkIndeterminate = false,
        AcquireProgress? downloads = null, string installReason = "", string downloadReason = "", string plan = "plan")
        => WabbajackModlistText.Status(error, install, installFraction, check, checkFraction, checkIndeterminate, downloads, installReason, downloadReason, plan);

    [Fact]
    public void Status_ShowsAnErrorAsAWarningAheadOfARunningInstall()
        => Status(error: "boom", install: "Writing files: 1 of 2", installFraction: 0.5)
            .Should().Be(new WabbajackStatus("boom", null, false, true));

    [Fact]
    public void Status_ShowsInstallProgressWithItsFraction()
        => Status(install: "Writing files: 1 of 2", installFraction: 0.5, check: "Reading the list")
            .Should().Be(new WabbajackStatus("Writing files: 1 of 2", 0.5, false, false));

    [Fact]
    public void Status_ShowsTheCheckOnceWhenBothReasonsRepeatTheCheckingSentence()
        => Status(check: "Hashing game files: 3.2 of 15.1 GB", checkFraction: 0.2, installReason: Checking, downloadReason: Checking)
            .Should().Be(new WabbajackStatus("Hashing game files: 3.2 of 15.1 GB", 0.2, false, false));

    [Fact]
    public void Status_PassesOnAnIndeterminateCheck()
        => Status(check: "Reading the list", checkIndeterminate: true).IsIndeterminate.Should().BeTrue();

    [Fact]
    public void Status_ShowsDownloadProgressWhileArchivesAreStillComing()
        => Status(downloads: new AcquireProgress(1, 4), installReason: "later")
            .Should().Be(new WabbajackStatus("Downloading archives: 1 of 4", 0.25, false, false));

    [Fact]
    public void Status_IgnoresFinishedDownloadsAndFallsToTheReason()
        => Status(downloads: new AcquireProgress(4, 4), installReason: "Get what is missing.")
            .Text.Should().Be("Get what is missing.");

    [Fact]
    public void Status_PrefersInstallsReasonOverDownloadsBecauseItNamesTheNextStep()
        => Status(installReason: "Get what is missing.", downloadReason: "Every archive is already in your library.")
            .Should().Be(new WabbajackStatus("Get what is missing.", null, false, false));

    [Fact]
    public void Status_FallsToDownloadsReasonWhenInstallIsAllowed()
        => Status(downloadReason: "Every archive is already in your library.").Text.Should().Be("Every archive is already in your library.");

    [Fact]
    public void Status_FallsToThePlanSummaryWhenBothButtonsAreOn()
        => Status(plan: "3 of 9 archives still to get").Should().Be(new WabbajackStatus("3 of 9 archives still to get", null, false, false));

    [Fact]
    public void IsLongDescription_IsTrueForLongTextAndForSeveralLines()
    {
        WabbajackModlistText.IsLongDescription("A short line.").Should().BeFalse();
        WabbajackModlistText.IsLongDescription(new string('a', 241)).Should().BeTrue();
        WabbajackModlistText.IsLongDescription("One\nTwo").Should().BeTrue();
    }

    [Fact]
    public void Blurb_TrimsTheDescription_AndKeepsOnlyAnHttpsReadme()
    {
        WabbajackModlistText.Blurb("  A list.\n", "https://example.com/readme").Should()
            .Be(new WabbajackModlistBlurb("A list.", new Uri("https://example.com/readme")));
        WabbajackModlistText.Blurb("x", "").Readme.Should().BeNull();
        WabbajackModlistText.Blurb("x", "README.md").Readme.Should().BeNull();
        WabbajackModlistText.Blurb("x", "http://example.com/readme").Readme.Should().BeNull("only https links are opened from the page");
    }
}
