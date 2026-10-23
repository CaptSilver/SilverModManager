using FluentAssertions;
using NexusMods.App.UI.Pages.Wabbajack;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Wabbajack.Install;

namespace NexusMods.UI.Tests.Pages;

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
        var a = LoadoutId.From(NexusMods.MnemonicDB.Abstractions.EntityId.From(1));
        var b = LoadoutId.From(NexusMods.MnemonicDB.Abstractions.EntityId.From(2));
        var c = LoadoutId.From(NexusMods.MnemonicDB.Abstractions.EntityId.From(3));

        var chosen = WabbajackModlistText.LoadoutsFor([(a, skyrim), (b, fallout), (c, skyrim)], skyrim);

        chosen.Should().Equal(a, c);
    }
}
