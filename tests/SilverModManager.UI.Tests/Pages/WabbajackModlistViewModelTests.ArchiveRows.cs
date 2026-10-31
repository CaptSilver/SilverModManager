using System.Reactive.Linq;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using ReactiveUI;
using SilverModManager.App.UI.Pages.Wabbajack;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;

namespace SilverModManager.UI.Tests.Pages;

// The archive table of a big list: a change to one archive touches that row, and only the rows on screen exist.
public partial class WabbajackModlistViewModelTests
{
    private static ArchivePlan BigPlan(int count, ArchiveState state = ArchiveState.Automatic)
        => Plan(Enumerable.Range(0, count).Select(i => Row(state, $"archive-{i}.7z")).ToArray());

    private static ArchivePlan WithState(ArchivePlan plan, int index, ArchiveState state, string? reason = null)
        => plan with { Rows = plan.Rows.Select((row, i) => i == index ? row with { State = state, Reason = reason } : row).ToArray() };

    [Fact]
    public void AForumRow_NamesItsSiteTheWayTheReasonDoes()
    {
        var hash = FileIdentity.XxHash64(Hash.From(1UL));
        var url = new Uri("https://example.com/a");
        ArchivePlanRow PlanFor(DownloadState state) => new(new ArchiveEntry(hash, 1, "a.7z", "", state), ArchiveState.Manual, null, url, null);

        new WabbajackArchiveRow(PlanFor(new LoversLabState(url, "a.7z", 1, false))).Source.Should().Be("LoversLab");
        new WabbajackArchiveRow(PlanFor(new VectorPlexusState(url, "a.7z", 1, false))).Source.Should().Be("VectorPlexus");
    }

    [Fact]
    public async Task OneArchiveChanging_UpdatesThatRowInPlace_AndLeavesTheRestAlone()
    {
        var plan = BigPlan(5);
        var (vm, _, acquirer) = await Open(plan);
        using var activation = vm.Activator.Activate();
        var before = vm.Archives.ToArray();
        var changes = before.Select(_ => new List<string?>()).ToArray();
        for (var i = 0; i < before.Length; i++)
        {
            var seen = changes[i];
            ((INotifyPropertyChanged)before[i]).PropertyChanged += (_, e) => seen.Add(e.PropertyName);
        }

        acquirer.Change(WithState(plan, 2, ArchiveState.Have));

        vm.Archives.Should().HaveCount(5);
        for (var i = 0; i < before.Length; i++) vm.Archives[i].Should().BeSameAs(before[i], "the row for an archive stays the same object");
        vm.Archives[2].Plan.State.Should().Be(ArchiveState.Have);
        vm.Archives[2].StateText.Should().Be("In library");
        changes[2].Should().Contain(nameof(WabbajackArchiveRow.StateText));
        foreach (var i in new[] { 0, 1, 3, 4 }) changes[i].Should().BeEmpty("only the archive that changed is told about it");
    }

    [Fact]
    public async Task OneArchiveChanging_DoesNotReplaceTheArchiveList()
    {
        var plan = BigPlan(5);
        var (vm, _, acquirer) = await Open(plan);
        using var activation = vm.Activator.Activate();
        var replaced = 0;
        using var subscription = vm.WhenAnyValue(x => x.Archives).Skip(1).Subscribe(_ => replaced++);

        acquirer.Change(WithState(plan, 1, ArchiveState.Manual, "hash mismatch"));

        replaced.Should().Be(0);
        vm.Archives[1].Reason.Should().Be("hash mismatch");
    }

    [Fact]
    public async Task AnotherSetOfArchives_GetsNewRows()
    {
        var plan = BigPlan(3);
        var (vm, service, acquirer) = await Open(plan);
        using var activation = vm.Activator.Activate();
        var before = vm.Archives.ToArray();

        acquirer.Change(Plan(Row(ArchiveState.Automatic, "other-0.7z"), Row(ArchiveState.Automatic, "other-1.7z")));

        // A plan of other archives is not this page's plan at all; the page keeps what it has.
        vm.Archives.Should().Equal(before);
        service.Plan = Plan(Row(ArchiveState.Automatic, "other-0.7z"));
        await vm.SelectLoadoutCommand.Execute(vm.SelectedLoadout!).FirstAsync();
        vm.Archives.Should().ContainSingle().Which.Name.Should().Be("other-0.7z");
    }

    [Fact]
    public async Task ARowWithAReasonAndANote_ShowsBoth()
    {
        var row = Row(ArchiveState.Manual, "both.7z") with { Reason = "hash mismatch", Note = "The list still takes a file from it" };
        var (vm, _, _) = await Open(Plan(row, Row(ArchiveState.Have, "none.7z"), Row(ArchiveState.Have, "note.7z") with { Note = "only a note" }));

        vm.Archives[0].Reason.Should().Contain("hash mismatch").And.Contain("The list still takes a file from it");
        vm.Archives[1].Reason.Should().BeEmpty();
        vm.Archives[2].Reason.Should().Be("only a note");
    }

    [Fact]
    public async Task ManyChangesInARow_AreAppliedAsTheLastOne_WithoutRunningThePageSummaryForEach()
    {
        var plan = BigPlan(20);
        var (vm, _, acquirer) = await Open(plan);
        using var activation = vm.Activator.Activate();
        var summaries = 0;
        using var subscription = vm.WhenAnyValue(x => x.AcquireText).Skip(1).Subscribe(_ => summaries++);

        var current = plan;
        for (var i = 0; i < 20; i++)
        {
            current = WithState(current, i, ArchiveState.Have);
            acquirer.Change(current);
        }

        await WaitUntil(() => vm.Archives.All(row => row.Plan.State == ArchiveState.Have));
        summaries.Should().BeLessThan(10, "a burst of downloads finishing is one update of the page, not twenty");
    }

    // The lines of the archive table; the needs-you section above it builds lines of its own for the same rows.
    private static IEnumerable<Control> Lines(WabbajackModlistView view) => view.FindControl<ItemsControl>("ArchiveRows")!.GetVisualDescendants().OfType<WabbajackArchiveLine>();

    [Fact]
    public async Task AThousandArchives_RealizeOnlyTheRowsInView()
    {
        var (vm, _, _) = await Open(BigPlan(1153));
        var view = await ShowView(vm);

        await EventuallyOnUi(() =>
        {
            Render((Window)view.GetVisualRoot()!);
            var realized = Lines(view).Count();
            realized.Should().BeGreaterThan(0).And.BeLessThan(100);
            view.FindControl<ItemsControl>("ArchiveRows")!.ItemsPanelRoot.Should().BeOfType<Avalonia.Controls.VirtualizingStackPanel>();
        });
    }

    [Fact]
    public async Task AnArchiveChanging_UpdatesItsRealizedLine_WithoutRecreatingAnyLine()
    {
        var plan = BigPlan(1153, ArchiveState.Browser);
        var (vm, _, acquirer) = await Open(plan);
        using var activation = vm.Activator.Activate();
        var view = await ShowView(vm);
        Control[] before = [];
        await EventuallyOnUi(() =>
        {
            Render((Window)view.GetVisualRoot()!);
            before = Lines(view).ToArray();
            before.Should().NotBeEmpty();
        });

        acquirer.Change(WithState(plan, 0, ArchiveState.Manual, "hash mismatch"));

        await EventuallyOnUi(() =>
        {
            Render((Window)view.GetVisualRoot()!);
            var after = Lines(view).ToArray();
            after.Should().Equal(before, "a state change must not rebuild the lines");
            var texts = TextsIn(after[0]);
            texts.Should().Contain("Needs a manual download").And.Contain("hash mismatch");
            TextsIn(after[1]).Should().Contain("Needs a download in your browser");
        });
    }

    [Fact]
    public async Task TheStateColumn_DoesNotCutOffItsLongestText()
    {
        var (vm, _, _) = await Open(BigPlan(3, ArchiveState.Browser));
        var view = await ShowView(vm);

        await EventuallyOnUi(() =>
        {
            Render((Window)view.GetVisualRoot()!);
            var block = Lines(view).First().GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Needs a download in your browser");
            block.TextTrimming.Should().Be(Avalonia.Media.TextTrimming.None);
            block.DesiredSize.Width.Should().BeLessThanOrEqualTo(block.Bounds.Width + 0.5);
            block.TextLayout.TextLines.Count.Should().BeLessThanOrEqualTo(2);
        });
    }

    private static ArchivePlan SignInPlan(int count)
        => Plan(Enumerable.Range(0, count).Select(i => Row(ArchiveState.SignIn, $"archive-{i}.7z") with { Reason = "Sign in to LoversLab to download this", Page = new Uri("https://www.loverslab.com/files/file/1-x/") }).ToArray());

    [Fact]
    public void ASignInRowWithAPageCanOpenItsPage()
    {
        new WabbajackArchiveRow(SignInPlan(1).Rows[0]).CanOpenPage.Should().BeTrue();
        new WabbajackArchiveRow(SignInPlan(1).Rows[0] with { Page = null }).CanOpenPage.Should().BeFalse();
    }

    [Fact]
    public async Task ASignInRow_ShowsItsStateUncutAndKeepsItsOpenPageButton()
    {
        var (vm, _, _) = await Open(SignInPlan(3));
        var view = await ShowView(vm);

        await EventuallyOnUi(() =>
        {
            Render((Window)view.GetVisualRoot()!);
            var line = Lines(view).First();
            var block = line.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Needs you to sign in");
            block.TextTrimming.Should().Be(Avalonia.Media.TextTrimming.None);
            block.DesiredSize.Width.Should().BeLessThanOrEqualTo(block.Bounds.Width + 0.5);
            block.TextLayout.TextLines.Count.Should().BeLessThanOrEqualTo(2);
            line.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Open page")).IsVisible.Should().BeTrue();
        });
    }
}
