using FluentAssertions;
using SilverModManager.App.UI.Pages.Wabbajack;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
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
    public void DownloadBlockedReason_SaysAnInstallIsRunningBeforeAnythingElse()
        => WabbajackModlistText.DownloadBlockedReason(false, false, true, false, Plan(Row(ArchiveState.Browser)), isInstalling: true).Should().Be("An install is running.");

    [Fact]
    public void Progress_NamesEachOfTheFourPhases()
    {
        WabbajackModlistText.Progress(Progress(InstallPhase.Check)).Should().Be("Checking the list");
        WabbajackModlistText.Progress(Progress(InstallPhase.Resolve, 3, 40)).Should().Be("Locating files in archives: 3 of 40");
        WabbajackModlistText.Progress(Progress(InstallPhase.Write, 12, 40)).Should().Be("Writing files: 12 of 40");
        WabbajackModlistText.Progress(Progress(InstallPhase.Order)).Should().Be("Ordering mods and plugins");
    }

    [Fact]
    public void Progress_NamesTheArchiveBeingBuiltAndHowManyOfItsFilesAreIn()
        => WabbajackModlistText.Progress(Progress(InstallPhase.Write, 95, 100) with { Archive = new ModlistInstallProgress.ArchiveBuild("pack.bsa", 3, 12, 40, 58) })
            .Should().Be("Building archive 3 of 12: pack.bsa (40 of 58 files)");

    [Fact]
    public void Fraction_IsTheShareOfTheCurrentPhaseAndZeroWhenThePhaseHasNoItems()
    {
        WabbajackModlistText.Fraction(Progress(InstallPhase.Write, 10, 40)).Should().BeApproximately(0.25, 1e-9);
        WabbajackModlistText.Fraction(Progress(InstallPhase.Order)).Should().Be(0);
    }

    [Fact]
    public void Fraction_CountsTheFilesPackedIntoRunningArchivesSoTheBarMovesWhileOneIsBuilt()
    {
        var building = Progress(InstallPhase.Write, 20, 40) with { ArchiveItems = 0.5 };

        WabbajackModlistText.Fraction(building).Should().BeApproximately(20.5 / 40, 1e-9);
        WabbajackModlistText.Fraction(building with { DoneItems = 40, ArchiveItems = 0.99 }).Should().Be(1);
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
            .Should().Be("Pick a loadout to install the collection into.");

    [Fact]
    public void InstallBlockedReason_CountsOnlyTheMissingArchiveNotTheGameFilesOfAnotherVersion()
    {
        var plan = Plan(Row(ArchiveState.Manual, "a"), Row(ArchiveState.GameFileVersion, "b"), Row(ArchiveState.GameFileVersion, "c"), Row(ArchiveState.Have, "d"));

        var reason = WabbajackModlistText.InstallBlockedReason(true, false, true, false, plan, false);

        reason.Should().Be("1 of 4 archives is not in your library yet. Get what is missing, or tick \"Skip files from missing archives\".");
    }

    [Fact]
    public void InstallBlockedReason_IsEmptyWhenOnlyGameFilesDiffer()
        => WabbajackModlistText.InstallBlockedReason(true, false, true, false, Plan(Row(ArchiveState.GameFileVersion), Row(ArchiveState.GameFileVersion, "b"), Row(ArchiveState.Have, "c")), false)
            .Should().BeEmpty();

    [Fact]
    public void InstallBlockedReason_CountsAGameFileVersionRowWithALibraryFileNowhere()
    {
        // A differing game file that also holds a library copy is available to the install, so it must not turn the count negative.
        var plan = Plan(Row(ArchiveState.Automatic, "a"), Row(ArchiveState.GameFileVersion, "b") with { Library = default(LibraryFile.ReadOnly) });

        WabbajackModlistText.InstallBlockedReason(true, false, true, false, plan, false)
            .Should().Be("1 of 2 archives is not in your library yet. Get what is missing, or tick \"Skip files from missing archives\".");
    }

    [Fact]
    public void IsAvailable_AgreesWithTheJobsArchiveCheckForEveryState()
    {
        foreach (var state in Enum.GetValues<ArchiveState>())
        {
            var plan = Plan(Row(state));
            WabbajackModlistText.IsAvailable(plan.Rows[0]).Should().Be(!ArchiveCheck.Of(plan).Missing.Contains(plan.Rows[0]), $"the page and the job must agree on {state}");
        }
    }

    [Fact]
    public void PlanSummary_DoesNotCountDifferingGameFilesAsStillToGet()
    {
        WabbajackModlistText.PlanSummary(Plan(Row(ArchiveState.Have), Row(ArchiveState.GameFileVersion, "b"))).Should().Be("Every archive is available");
        WabbajackModlistText.PlanSummary(Plan(Row(ArchiveState.Manual), Row(ArchiveState.GameFileVersion, "b"))).Should().Be("1 of 2 archives still to get");
    }

    [Fact]
    public void GameFilesNote_CountsDifferingGameFilesInSingularAndPlural()
    {
        var one = WabbajackModlistText.GameFilesNote(Plan(Row(ArchiveState.Have), Row(ArchiveState.GameFileVersion, "b")));
        var many = WabbajackModlistText.GameFilesNote(Plan(Row(ArchiveState.GameFileVersion, "a"), Row(ArchiveState.GameFileVersion, "b"), Row(ArchiveState.GameFileVersion, "c")));

        one.Should().Be(new WabbajackNote("Your game is a different version from the one the list was made for, so 1 of its files is left out and your game's own copy is used instead. Anything the list builds from it is listed under \"Files that were skipped\" after the install.", true));
        many.Should().Be(new WabbajackNote("Your game is a different version from the one the list was made for, so 3 of its files are left out and your game's own copies are used instead. Anything the list builds from them is listed under \"Files that were skipped\" after the install.", true));
    }

    private static ArchivePlanRow Replaced(string name, GameStore? store = null, bool unknownStore = false)
        => Row(ArchiveState.GameFileVersion, name) with { GameFileReplaced = true, InstalledFrom = unknownStore ? null : store ?? GameStore.Steam };

    [Fact]
    public void GameFilesNote_ForReplacedFilesAloneHedgesTheCauseInSingularAndPlural()
    {
        var one = WabbajackModlistText.GameFilesNote(Plan(Row(ArchiveState.Have), Replaced("b")));
        var many = WabbajackModlistText.GameFilesNote(Plan(Replaced("a"), Replaced("b"), Replaced("c")));

        one.Should().Be(new WabbajackNote("1 of your game's files is not the original the list needs: something replaced it, most likely an earlier install, and the app has no copy to go back to, so it is left out. Verify the game's files in Steam to put it back before you install; until then anything the list builds from it is listed under \"Files that were skipped\" after the install.", true));
        many.Should().Be(new WabbajackNote("3 of your game's files are not the originals the list needs: something replaced them, most likely an earlier install, and the app has no copies to go back to, so they are left out. Verify the game's files in Steam to put them back before you install; until then anything the list builds from them is listed under \"Files that were skipped\" after the install.", true));
    }

    [Fact]
    public void GameFilesNote_ForReplacedFilesOfAGogGameNamesHeroicOrTheStoresLauncher()
    {
        var note = WabbajackModlistText.GameFilesNote(Plan(Replaced("a", GameStore.GOG)));

        note.Text.Should().Contain("Verify the game's files in Heroic or the store's launcher to put it back").And.NotContain("Steam");
    }

    [Fact]
    public void GameFilesNote_ForReplacedFilesOfAnEpicGameNamesHeroicOrTheStoresLauncher()
        => WabbajackModlistText.GameFilesNote(Plan(Replaced("a", GameStore.EGS))).Text.Should().Contain("in Heroic or the store's launcher");

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Manually Added")]
    public void GameFilesNote_ForReplacedFilesOfAGameFromNoKnownStoreNeverNamesSteam(string store)
        => WabbajackModlistText.GameFilesNote(Plan(Replaced("a", GameStore.From(store)))).Text
            .Should().Contain("Verify the game's files with whatever installed the game to put it back").And.NotContain("Steam");

    [Fact]
    public void GameFilesNote_ForAReplacedFileWithNoStoreOnItsRowNeverNamesSteam()
        => WabbajackModlistText.GameFilesNote(Plan(Replaced("a", unknownStore: true))).Text
            .Should().Contain("with whatever installed the game").And.NotContain("Steam");

    [Fact]
    public void State_LabelsAReplacedGameFileApartFromADifferingOne()
    {
        WabbajackModlistText.State(Replaced("a")).Should().Be("Game file replaced");
        WabbajackModlistText.State(Row(ArchiveState.GameFileVersion, "b")).Should().Be("Game file differs");
    }

    [Fact]
    public void GameFilesNote_WhenSomeFilesDifferAndSomeAreReplacedSaysBoth()
    {
        var note = WabbajackModlistText.GameFilesNote(Plan(Row(ArchiveState.GameFileVersion, "a"), Replaced("b"), Replaced("c")));

        note.Text.Should().Be(
            "Your game is a different version from the one the list was made for, so 1 of its files is left out and your game's own copy is used instead. Anything the list builds from it is listed under \"Files that were skipped\" after the install. "
            + "2 of your game's files are not the originals the list needs: something replaced them, most likely an earlier install, and the app has no copies to go back to, so they are left out. Verify the game's files in Steam to put them back before you install; until then anything the list builds from them is listed under \"Files that were skipped\" after the install.");
        note.IsWarning.Should().BeTrue();
    }

    [Fact]
    public void GameFilesNote_LeavesOutReplacedFilesThatAreMissingFromTheGameFolder()
        => WabbajackModlistText.GameFilesNote(Plan(Replaced("a") with { GameFileAbsent = true })).Text.Should().BeEmpty();

    [Fact]
    public void GameFilesNote_LeavesOutGameFilesThatAreMissingFromTheGameFolder()
    {
        var missing = Row(ArchiveState.GameFileVersion, "b") with { GameFileAbsent = true };

        WabbajackModlistText.GameFilesNote(Plan(Row(ArchiveState.Have), missing)).Text.Should().BeEmpty("there is no copy of the game's own to use instead");
        WabbajackModlistText.GameFilesNote(Plan(missing, Row(ArchiveState.GameFileVersion, "c"))).Text.Should().Contain("so 1 of its files is left out");
    }

    [Fact]
    public void GameFilesNote_IsEmptyWhenNoGameFileDiffers()
        => WabbajackModlistText.GameFilesNote(Plan(Row(ArchiveState.Have), Row(ArchiveState.GameFile, "b"))).Text.Should().BeEmpty();

    [Fact]
    public void GameFilesNote_IsEmptyWhileAnyArchiveIsStillToGet()
    {
        var plan = Plan(Row(ArchiveState.Manual), Row(ArchiveState.GameFileVersion, "b"));

        WabbajackModlistText.GameFilesNote(plan).Text.Should().BeEmpty("it would hide how many archives are still to get, and their Show them button");
        WabbajackModlistText.PlanSummary(plan).Should().Be("1 of 2 archives still to get");
    }

    [Fact]
    public void InstallBlockedReason_SaysOneMissingArchiveInTheSingular()
        => WabbajackModlistText.InstallBlockedReason(true, false, true, false, Plan(Row(ArchiveState.Manual), Row(ArchiveState.Have, "b")), false)
            .Should().Contain("1 of 2 archives is not in your library yet");

    private static ArchivePlanRow HeldIncomplete(string name)
        => Row(ArchiveState.Manual, name) with { Library = default(LibraryFile.ReadOnly) };

    [Fact]
    public void InstallBlockedReason_CountsAHeldCopyThatLacksNestedArchivesApartFromMissingArchives()
    {
        var plan = Plan(Row(ArchiveState.Manual, "a"), HeldIncomplete("b"), Row(ArchiveState.Have, "c"));

        var reason = WabbajackModlistText.InstallBlockedReason(true, false, true, false, plan, false);

        reason.Should().Contain("1 of 3 archives is not in your library yet")
            .And.Contain("1 archive needs downloading again: the library copy is missing archives packed inside it.")
            .And.Contain("Get what is missing");
    }

    [Fact]
    public void InstallBlockedReason_DoesNotCallAHeldIncompleteArchiveNotInTheLibrary()
    {
        var plan = Plan(HeldIncomplete("a"), HeldIncomplete("b"), Row(ArchiveState.Have, "c"));

        var reason = WabbajackModlistText.InstallBlockedReason(true, false, true, false, plan, false);

        reason.Should().NotContain("not in your library yet")
            .And.Contain("2 archives need downloading again: the library copies are missing archives packed inside them.")
            .And.Contain("Get what is missing");
    }

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
            .Should().StartWith("The missing archive cannot be fetched by the app").And.NotContain("game file");
        WabbajackModlistText.DownloadBlockedReason(false, false, true, false, Plan(Row(ArchiveState.GameFileVersion, "b")))
            .Should().Be("Every archive is already in your library or is a file of your game.");
    }

    [Fact]
    public void DownloadBlockedReason_SaysTheListIsBeingCheckedWhilePreparingTheDownloads()
        => WabbajackModlistText.DownloadBlockedReason(false, true, true, false, Plan(Row(ArchiveState.Automatic))).Should().StartWith("Checking the list");

    [Fact]
    public void InstallBlockedReason_OnlyTellsYouToGetArchivesWhenSomeAreMissing()
    {
        WabbajackModlistText.InstallBlockedReason(true, false, true, false, Plan(Row(ArchiveState.GameFileVersion), Row(ArchiveState.Have, "b")), false).Should().BeEmpty();

        var missing = WabbajackModlistText.InstallBlockedReason(true, false, true, false, Plan(Row(ArchiveState.Manual), Row(ArchiveState.GameFileVersion, "b")), false);
        missing.Should().Contain("Get what is missing").And.NotContain("Put back the game files");
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

    [Fact]
    public void Counts_PutARowThatNeedsASignInWithTheOnesTheAppCanGet()
    {
        var counts = WabbajackModlistText.Counts(Plan(Row(ArchiveState.Automatic), Row(ArchiveState.SignIn, "b"), Row(ArchiveState.Manual, "c")));

        counts.Should().Be(new WabbajackPlanCounts(Ready: 0, ToGet: 2, ByHand: 1, GameFilesDiffer: 0));
    }

    [Fact]
    public void State_SaysASignInRowNeedsTheUserToSignIn()
        => WabbajackModlistText.State(Row(ArchiveState.SignIn)).Should().Be("Needs you to sign in");

    [Fact]
    public void DownloadBlockedReason_SaysToSignInWhenThatIsAllThatIsLeft()
    {
        WabbajackModlistText.DownloadBlockedReason(false, false, true, false, Plan(Row(ArchiveState.SignIn)))
            .Should().Contain("sign in").And.NotContain("already in your library");
        WabbajackModlistText.DownloadBlockedReason(false, false, true, false, Plan(Row(ArchiveState.Automatic), Row(ArchiveState.SignIn, "b")))
            .Should().BeEmpty("the rest can still be downloaded");
    }

    [Fact]
    public void ModOrganizerItselfIsCountedApartAndNeverBlocksAnInstall()
    {
        var plan = Plan(Row(ArchiveState.Have), Row(ArchiveState.NotNeeded));

        WabbajackModlistText.Counts(plan).Should().Be(new WabbajackPlanCounts(Ready: 1, ToGet: 0, ByHand: 0, GameFilesDiffer: 0, NotNeeded: 1));
        WabbajackModlistText.IsAvailable(plan.Rows[1]).Should().BeTrue();
        WabbajackModlistText.CanInstall(plan, skipMissingArchives: false).Should().BeTrue();
        WabbajackModlistText.InstallBlockedReason(true, false, true, false, plan, false).Should().BeEmpty();
    }

    [Fact]
    public void ModOrganizerItselfSaysSoInTheStateColumnAndIsLeftOutOfTheMissingCount()
    {
        var plan = Plan(Row(ArchiveState.Manual), Row(ArchiveState.NotNeeded), Row(ArchiveState.NotNeeded));

        WabbajackModlistText.State(plan.Rows[1]).Should().Be("Not needed: Mod Organizer 2 itself");
        WabbajackModlistText.InstallBlockedReason(true, false, true, false, plan, false).Should().Contain("1 of 1 archives is not in your library yet");
    }

    [Fact]
    public void ModOrganizerItselfAddsNothingToTheExtractedSize()
    {
        var (size, lowerBound) = WabbajackModlistText.ExtractedSize(Plan(Row(ArchiveState.NotNeeded)));

        size.Value.Should().Be(0);
        lowerBound.Should().BeFalse();
    }

    private const string Checking = "Checking the list against your library and game files. This can take a while for a big list.";

    private static WabbajackStatus Status(
        string error = "", string install = "",
        string check = "", double checkFraction = 0, bool checkIndeterminate = false,
        AcquireProgress? downloads = null, bool isDownloading = true, string installReason = "", string downloadReason = "", string plan = "plan", string gameFilesNote = "")
        => WabbajackModlistText.Status(error, install, check, checkFraction, checkIndeterminate, isDownloading, downloads, installReason, new WabbajackNote(gameFilesNote, gameFilesNote != ""), downloadReason, plan);

    [Fact]
    public void Status_ShowsAnErrorAsAWarningAheadOfARunningInstall()
        => Status(error: "boom", install: "Writing files: 1 of 2")
            .Should().Be(new WabbajackStatus("boom", null, false, true));

    [Fact]
    public void Status_SaysNothingWhileAnInstallRunsBecauseTheHeaderShowsIt()
        => Status(install: "Writing files: 1 of 2", check: "Reading the list")
            .Should().Be(new WabbajackStatus("", null, false, false));

    [Fact]
    public void Status_SaysNothingWhileTheListIsCheckedBecauseTheHeaderShowsIt()
        => Status(check: "Hashing game files: 3.2 of 15.1 GB", checkFraction: 0.2, installReason: Checking, downloadReason: Checking)
            .Should().Be(new WabbajackStatus("", null, false, false));

    [Fact]
    public void Status_SaysNothingWhileArchivesAreStillComingBecauseTheHeaderShowsIt()
        => Status(downloads: new AcquireProgress(1, 4), installReason: "later")
            .Should().Be(new WabbajackStatus("", null, false, false));

    [Fact]
    public void Status_FallsToTheReasonWhenABatchEndedPartway()
        => Status(downloads: new AcquireProgress(2, 3), isDownloading: false, installReason: "Get what is missing.")
            .Text.Should().Be("Get what is missing.");

    [Fact]
    public void Status_StillShowsAnErrorWhileTheListIsChecked()
        => Status(error: "boom", check: "Reading the list").Should().Be(new WabbajackStatus("boom", null, false, true));

    [Fact]
    public void Status_IgnoresFinishedDownloadsAndFallsToTheReason()
        => Status(downloads: new AcquireProgress(4, 4), installReason: "Get what is missing.")
            .Text.Should().Be("Get what is missing.");

    private static WabbajackStatus Running(
        string install = "", double installFraction = 0,
        string check = "", double checkFraction = 0, bool checkIndeterminate = false,
        AcquireProgress? downloads = null, bool isDownloading = true)
        => WabbajackModlistText.Running(new WabbajackInstallStatus(install, installFraction), check, checkFraction, checkIndeterminate, isDownloading, downloads);

    [Fact]
    public void Running_IgnoresADownloadCountOnceNothingIsDownloading()
        => Running(downloads: new AcquireProgress(2, 3), isDownloading: false)
            .Should().Be(new WabbajackStatus("", null, false, false));

    [Fact]
    public void Running_IsEmptyWhenNothingRuns()
        => Running().Should().Be(new WabbajackStatus("", null, false, false));

    [Fact]
    public void Running_ShowsTheInstallLineAndBarAheadOfTheCheck()
        => Running(install: "Writing files: 1 of 4", installFraction: 0.25, check: "Reading the list")
            .Should().Be(new WabbajackStatus("Writing files: 1 of 4", 0.25, false, false));

    [Fact]
    public void Running_ShowsTheCheckWithItsFraction()
        => Running(check: "Hashing game files: 3.2 of 15.1 GB", checkFraction: 0.2)
            .Should().Be(new WabbajackStatus("Hashing game files: 3.2 of 15.1 GB", 0.2, false, false));

    [Fact]
    public void Running_PassesOnAnIndeterminateCheck()
        => Running(check: "Reading the list", checkIndeterminate: true).IsIndeterminate.Should().BeTrue();

    [Fact]
    public void Running_ShowsDownloadProgressWhileArchivesAreStillComing()
        => Running(downloads: new AcquireProgress(1, 4))
            .Should().Be(new WabbajackStatus("Downloading archives: 1 of 4", 0.25, false, false));

    [Fact]
    public void Running_IgnoresFinishedDownloads()
        => Running(downloads: new AcquireProgress(4, 4)).Should().Be(new WabbajackStatus("", null, false, false));

    [Fact]
    public void Status_PrefersInstallsReasonOverDownloadsBecauseItNamesTheNextStep()
        => Status(installReason: "Get what is missing.", downloadReason: "Every archive is already in your library.")
            .Should().Be(new WabbajackStatus("Get what is missing.", null, false, false));

    [Fact]
    public void Status_PutsTheGameFilesNoteAboveTheDownloadReasonAndBelowTheInstallReason()
    {
        Status(installReason: "Pick a loadout.", gameFilesNote: "Your game differs.", downloadReason: "Every archive is already in your library.")
            .Should().Be(new WabbajackStatus("Pick a loadout.", null, false, false));
        Status(gameFilesNote: "Your game differs.", downloadReason: "Every archive is already in your library.")
            .Should().Be(new WabbajackStatus("Your game differs.", null, false, true));
        Status(gameFilesNote: "", downloadReason: "Every archive is already in your library.").Text.Should().Be("Every archive is already in your library.");
        Status(error: "boom", gameFilesNote: "Your game differs.").Text.Should().Be("boom");
    }

    [Fact]
    public void Status_FallsToDownloadsReasonWhenInstallIsAllowed()
        => Status(downloadReason: "Every archive is already in your library.").Text.Should().Be("Every archive is already in your library.");

    [Fact]
    public void Status_FallsToThePlanSummaryWhenBothButtonsAreOn()
        => Status(plan: "3 of 9 archives still to get").Should().Be(new WabbajackStatus("3 of 9 archives still to get", null, false, false));

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
