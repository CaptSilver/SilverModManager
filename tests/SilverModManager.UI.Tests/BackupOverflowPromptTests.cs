using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Pages.MyGames;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.UI.Tests;

public class BackupOverflowPromptTests(IServiceProvider provider)
{
    private static readonly BackupOverflow Overflow = new(Adoptable: Size.From(15_748_000_000), AdoptableCount: 4506, MustBackUp: Size.Zero, MustBackUpCount: 0, Limit: Size.GB * 5, Operation: BackupOverflowOperation.Manage);

    private static BackupOverflowGroup Group(string folder, int count, Size size, BackupOverflowKind kind = BackupOverflowKind.GameFileChanged) => new(folder, count, size, kind);

    private static BackupOverflow Witcher(BackupOverflowKind kind = BackupOverflowKind.GameFileChanged, BackupOverflowOperation operation = BackupOverflowOperation.Launch) => new(
        Adoptable: Size.GB * 45, AdoptableCount: 95, MustBackUp: Size.Zero, MustBackUpCount: 0, Limit: Size.GB * 5, Operation: operation,
        Groups: [Group("content/content0", 20, Size.GB * 30, kind), Group("content/patch1", 60, Size.From(13_421_772_800), kind), Group("bin/x64", 15, Size.From(2_684_354_560), kind)],
        Examples: [new("content/content0/bundles/xml.bundle", Size.From(4_509_715_660), kind), new("bin/x64/witcher3.exe", Size.MB * 100, kind)],
        ByKind: new Dictionary<BackupOverflowKind, (int Count, Size Size)> { [kind] = (95, Size.GB * 45) });

    [Fact]
    public void WhenAdoptIsNotPossible_SaysTheFilesWouldBeDeleted()
    {
        var prompt = BackupOverflowPrompt.For("The Witcher 3", Overflow with { Adoptable = Size.Zero, AdoptableCount = 0, MustBackUp = Size.GB * 6, MustBackUpCount = 10 });

        prompt.Markdown.Should().Contain("deletes or replaces").And.NotContain("Adopt").And.NotContain("Keep them where they are");
        prompt.Buttons.Select(b => b.Text).Should().NotContain("Keep them where they are");
    }

    [Fact]
    public void TitleIsShortAndTheBodyCarriesTheNumbers()
    {
        var prompt = BackupOverflowPrompt.For("The Witcher 3", Overflow);

        prompt.Title.Length.Should().BeLessThan(48);
        prompt.Title.Should().NotContain("The Witcher 3").And.NotMatchRegex("[0-9]");
        prompt.Markdown.Should().StartWith("The Witcher 3 has 4506 files (14.7 GB)").And.NotContain("14.666");
    }

    [Fact]
    public void MarkdownSaysWhenKeptFilesGetBackedUpAndNamesNoCommand()
    {
        var md = BackupOverflowPrompt.For("The Witcher 3", Overflow).Markdown;

        md.Should().Contain("switching loadouts").And.Contain("5.0 GB").And.NotContain("cannot restore them");
        md.Should().NotContain("silvermod").And.NotContain("backup-game-files").And.NotContain("`");
    }

    [Fact]
    public void MixedOverflow_SaysWhichPartIsBackedUpEitherWay()
        => BackupOverflowPrompt.For("X", Overflow with { MustBackUp = Size.KB * 12, MustBackUpCount = 2 }).Markdown.Should().Contain("2 files").And.Contain("either way");

    [Fact]
    public void ButtonsOfferKeepBackUpAndDontAddTheGame()
    {
        var buttons = BackupOverflowPrompt.For("The Witcher 3", Overflow).Buttons;

        buttons.Select(b => b.Text).Should().Equal("Keep them where they are", "Back up 14.7 GB", "Don't add the game");
    }

    [Fact]
    public void SizesReadTheWayTheRestOfTheAppShowsThem()
    {
        var md = BackupOverflowPrompt.For("The Witcher 3", Overflow).Markdown;

        md.Should().Contain("5.0 GB").And.Contain("14.7 GB").And.NotContain("14.666");
        BackupOverflow.FormatSize(Size.From(15_748_000_000)).Should().Be("14.7 GB");
        BackupOverflow.FormatSize(Size.From(12_288)).Should().Be("12 KB");
    }

    [Theory]
    [InlineData(BackupOverflowOperation.Manage, "Don't add the game")]
    [InlineData(BackupOverflowOperation.Apply, "Don't apply")]
    [InlineData(BackupOverflowOperation.Launch, "Don't start the game")]
    [InlineData(BackupOverflowOperation.Switch, "Stay on this loadout")]
    [InlineData(BackupOverflowOperation.Reset, "Leave the folder as it is")]
    public void CancelButtonNamesWhatItStops(BackupOverflowOperation operation, string label)
    {
        var prompt = BackupOverflowPrompt.For("The Witcher 3", Overflow with { Operation = operation });

        prompt.Buttons.Last().Text.Should().Be(label);
        prompt.Buttons.Last().Id.Should().Be(ButtonDefinitionId.Cancel);
        prompt.Markdown.Should().Contain($"**{label}** changes nothing");
    }

    [Theory]
    [InlineData(BackupOverflowOperation.Manage, "Back up files before adding this game?")]
    [InlineData(BackupOverflowOperation.Apply, "Back up files before applying?")]
    [InlineData(BackupOverflowOperation.Launch, "Back up files before starting the game?")]
    [InlineData(BackupOverflowOperation.Switch, "Back up files before switching loadouts?")]
    [InlineData(BackupOverflowOperation.Reset, "Back up files before cleaning the folder?")]
    [InlineData(BackupOverflowOperation.AfterPlay, "Back up files the game wrote?")]
    public void TitleNamesTheOperation(BackupOverflowOperation operation, string title)
        => BackupOverflowPrompt.For("The Witcher 3", Overflow with { Operation = operation }).Title.Should().Be(title);

    [Fact]
    public void BodyNamesTheFoldersLargestFirstAndFoldsTheRest()
    {
        var groups = new[]
        {
            Group("a", 1, Size.GB * 1), Group("b", 1, Size.GB * 6), Group("c", 1, Size.GB * 5), Group("d", 1, Size.GB * 4), Group("e", 1, Size.GB * 3), Group("f", 1, Size.GB * 2),
        };

        var md = BackupOverflowPrompt.For("X", Witcher() with { Groups = groups }).Markdown;

        md.IndexOf("- b —", StringComparison.Ordinal).Should().BePositive().And.BeLessThan(md.IndexOf("- c —", StringComparison.Ordinal));
        md.Should().Contain("- e —").And.NotContain("- a —").And.Contain("and 1 more folder (1.0 GB).");
    }

    [Fact]
    public void BodyGivesTheFoldersWithCountsAndSizesAndExamplePaths()
    {
        var md = BackupOverflowPrompt.For("The Witcher 3: Wild Hunt", Witcher()).Markdown;

        md.Should().Contain("- content/content0 — 20 files, 30.0 GB").And.Contain("- bin/x64 — 15 files, 2.5 GB");
        md.Should().Contain("For example content/content0/bundles/xml.bundle (4.2 GB), bin/x64/witcher3.exe (100 MB).");
    }

    [Fact]
    public void AppearedFiles_AreSaidNotToBeMods()
        => BackupOverflowPrompt.For("X", Witcher(BackupOverflowKind.Appeared)).Markdown.Should().Contain("not from this app and not your mods");

    [Fact]
    public void ChangedGameFiles_AreSaidToBeAGameUpdate()
        => BackupOverflowPrompt.For("X", Witcher()).Markdown.Should().Contain("the game's own files, and they changed since the app last saw them. A game update does that.");

    [Fact]
    public void ChangedModFiles_AreSaidToHaveBeenEdited()
        => BackupOverflowPrompt.For("X", Witcher(BackupOverflowKind.ModFileChanged)).Markdown.Should().Contain("mod files that changed since the app put them in place");

    [Fact]
    public void MixedKinds_LeadWithTheLargest()
    {
        var overflow = Witcher() with
        {
            ByKind = new Dictionary<BackupOverflowKind, (int Count, Size Size)>
            {
                [BackupOverflowKind.Appeared] = (5, Size.GB * 5),
                [BackupOverflowKind.GameFileChanged] = (90, Size.GB * 40),
            },
        };

        var md = BackupOverflowPrompt.For("X", overflow).Markdown;

        md.Should().Contain("the game's own files, and they changed").And.Contain("5 files (5.0 GB) were put there by you, Steam or another program.");
        md.IndexOf("game's own files", StringComparison.Ordinal).Should().BeLessThan(md.IndexOf("were put there", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(BackupOverflowOperation.Manage)]
    [InlineData(BackupOverflowOperation.Apply)]
    [InlineData(BackupOverflowOperation.Launch)]
    [InlineData(BackupOverflowOperation.Switch)]
    [InlineData(BackupOverflowOperation.Reset)]
    [InlineData(BackupOverflowOperation.AfterPlay)]
    public void NoJargon(BackupOverflowOperation operation)
    {
        var overflows = new[]
        {
            Witcher(operation: operation),
            Witcher(BackupOverflowKind.Appeared, operation),
            Overflow with { Operation = operation, MustBackUp = Size.KB * 12, MustBackUpCount = 2 },
            Overflow with { Operation = operation, Adoptable = Size.Zero, AdoptableCount = 0, MustBackUp = Size.GB * 6, MustBackUpCount = 10 },
        };

        foreach (var md in overflows.Select(o => BackupOverflowPrompt.For("X", o).Markdown))
            md.ToLowerInvariant().Should().NotContainAny("added files", "adopt", "ingest", "manages", "silvermod", "`");
    }

    [Fact]
    public void LaunchOverTheCap_ReadsAsAQuestionAboutStartingTheGame()
    {
        var prompt = BackupOverflowPrompt.For("The Witcher 3: Wild Hunt", Witcher());

        prompt.Markdown.Should().Contain("then starts the game").And.Contain("**Don't start the game** changes nothing; the game is not started.");
        prompt.Markdown.Should().Contain("95 files (45.0 GB) in its folder that the app has no copy of");
    }

    [Fact]
    public void AfterPlay_WhenKeepingIsPossible_OffersKeepAndBackUpAndClosingKeeps()
    {
        var prompt = BackupOverflowPrompt.For("The Witcher 3", Witcher(operation: BackupOverflowOperation.AfterPlay));

        prompt.Buttons.Select(b => b.Text).Should().Equal("Keep them where they are", "Back up 45.0 GB");
        prompt.Markdown.Should().Contain("While The Witcher 3 ran, 95 files (45.0 GB) appeared or changed").And.Contain("Closing this window keeps them where they are.");
    }

    [Fact]
    public void AfterPlay_WhenKeepingWouldLoseFiles_OffersDecideLater()
    {
        var overflow = Overflow with { Operation = BackupOverflowOperation.AfterPlay, Adoptable = Size.Zero, AdoptableCount = 0, MustBackUp = Size.GB * 6, MustBackUpCount = 10 };

        var prompt = BackupOverflowPrompt.For("The Witcher 3", overflow);

        prompt.Buttons.Select(b => b.Text).Should().Equal("Back up 6.0 GB", "Decide later");
        prompt.Markdown.Should().Contain("leaves the loadout applied").And.Contain("asks again the next time you apply or switch");
    }

    [Fact]
    public void OnlyBackUpIsTheEnterKeyDefault()
    {
        var buttons = BackupOverflowPrompt.For("The Witcher 3", Overflow).Buttons;

        buttons.Where(b => b.ButtonAction == ButtonAction.Accept).Select(b => b.Text).Should().Equal("Back up 14.7 GB");
    }

    [Fact]
    public void GuiContainer_ResolvesTheDialogPrompt()
        => provider.GetRequiredService<IBackupOverflowPrompt>().Should().BeOfType<DialogBackupOverflowPrompt>();
}
