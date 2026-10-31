using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.VisualTree;
using System.Reactive;
using System.Reactive.Linq;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NSubstitute;
using SilverModManager.App.UI;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.Sdk.Hashes;
using SilverModManager.UI.Tests.Helpers;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;

namespace SilverModManager.UI.Tests.Pages;

// The "needs you" section and the filter over the archive table: which rows land in them, and that a row changing
// moves its own entry without the other rows being rebuilt.
public partial class WabbajackModlistViewModelTests
{
    private static ArchivePlanRow From(string host, ArchiveState state, string name, string? reason = null, string? page = null)
        => new(new ArchiveEntry(FileIdentity.XxHash64(Hash.From((ulong)name.GetHashCode())), 2_000_000, name, "", new ManualState(new Uri($"https://{host}/a"), "get it")),
            state, reason, page is null ? null : new Uri(page), null);

    // One row of every kind: held, automatic, automatic that failed, browser, manual, sign-in, and a game file.
    private static ArchivePlan Mixed() => Plan(
        From("zeta.example", ArchiveState.Have, "held.7z"),
        From("zeta.example", ArchiveState.Automatic, "auto.7z"),
        From("zeta.example", ArchiveState.Automatic, "failed.7z", "connection reset"),
        From("beta.example", ArchiveState.Manual, "manual-b.7z", "hash mismatch", "https://beta.example/files/1"),
        From("alpha.example", ArchiveState.Browser, "browser-a.7z"),
        From("alpha.example", ArchiveState.SignIn, "aaa-signin.7z", "Sign in to LoversLab to download this", "https://www.loverslab.com/files/file/1-x/"),
        From("zeta.example", ArchiveState.GameFile, "Skyrim.exe"));

    private static string[] Names(IEnumerable<WabbajackArchiveRow> rows) => rows.Select(row => row.Name).ToArray();

    [Fact]
    public async Task NeedsYou_HoldsTheRowsThatWaitForTheUser_AndAFailedAutomaticOne_AsTheSameRowObjects()
    {
        var (vm, _, _) = await Open(Mixed());

        Names(vm.NeedsYou).Should().BeEquivalentTo("browser-a.7z", "aaa-signin.7z", "manual-b.7z", "failed.7z");
        vm.NeedsYou.Should().OnlyContain(row => vm.Archives.Contains(row), "the section shows the table's own rows");
        vm.Archives.Should().HaveCount(7);
    }

    [Fact]
    public async Task NeedsYou_IsOrderedBySourceThenName()
    {
        var (vm, _, _) = await Open(Mixed());

        Names(vm.NeedsYou).Should().Equal("aaa-signin.7z", "browser-a.7z", "manual-b.7z", "failed.7z");
    }

    [Fact]
    public async Task TheHeading_CountsTheRows_AndTheSectionIsOffWithoutAny()
    {
        var (vm, _, acquirer) = await Open(Plan(From("a.example", ArchiveState.Manual, "one.7z"), From("a.example", ArchiveState.Have, "two.7z")));
        using var activation = vm.Activator.Activate();

        vm.HasNeedsYou.Should().BeTrue();
        vm.NeedsYouHeading.Should().Be("1 archive needs you");

        acquirer.Change(Plan(From("a.example", ArchiveState.Manual, "one.7z"), From("a.example", ArchiveState.Manual, "two.7z")));
        vm.NeedsYouHeading.Should().Be("2 archives need you");

        // A burst of changes is shown as the last one a moment later.
        acquirer.Change(Plan(From("a.example", ArchiveState.Have, "one.7z"), From("a.example", ArchiveState.Have, "two.7z")));
        await WaitUntil(() => !vm.HasNeedsYou);
        vm.HasNeedsYou.Should().BeFalse();
        vm.NeedsYou.Should().BeEmpty();
    }

    [Fact]
    public async Task ARowThatArrivesInTheLibrary_LeavesTheSection_WithoutTheOtherRowsBeingRebuilt()
    {
        var plan = Mixed();
        var (vm, _, acquirer) = await Open(plan);
        using var activation = vm.Activator.Activate();
        var section = vm.NeedsYou;
        var before = section.ToArray();
        var archives = vm.Archives.ToArray();
        var events = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyCollectionChanged)section).CollectionChanged += (_, e) => events.Add(e);

        var credited = plan.Rows.ToList().FindIndex(row => row.Archive.Name == "manual-b.7z");
        acquirer.Change(WithState(plan, credited, ArchiveState.Have));

        vm.NeedsYou.Should().BeSameAs(section, "the section's list is edited, not replaced");
        events.Should().ContainSingle().Which.Action.Should().Be(NotifyCollectionChangedAction.Remove);
        vm.NeedsYou.Should().Equal(before.Where(row => row.Name != "manual-b.7z"), "the other entries are the same row objects in the same order");
        vm.Archives.Should().Equal(archives);
    }

    [Fact]
    public async Task ARowThatFails_JoinsTheSectionAtItsPlace()
    {
        var plan = Mixed();
        var (vm, _, acquirer) = await Open(plan);
        using var activation = vm.Activator.Activate();
        var before = vm.NeedsYou.ToArray();
        var events = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyCollectionChanged)vm.NeedsYou).CollectionChanged += (_, e) => events.Add(e);

        var auto = plan.Rows.ToList().FindIndex(row => row.Archive.Name == "auto.7z");
        acquirer.Change(plan with { Rows = plan.Rows.Select((row, i) => i == auto ? row with { State = ArchiveState.Manual, Reason = "hash mismatch" } : row).ToArray() });

        Names(vm.NeedsYou).Should().Equal("aaa-signin.7z", "browser-a.7z", "manual-b.7z", "auto.7z", "failed.7z");
        events.Should().ContainSingle().Which.Action.Should().Be(NotifyCollectionChangedAction.Add);
        vm.NeedsYou.Where(row => row.Name != "auto.7z").Should().Equal(before);
    }

    [Theory]
    [InlineData(WabbajackArchiveFilter.All, new[] { "held.7z", "auto.7z", "failed.7z", "manual-b.7z", "browser-a.7z", "aaa-signin.7z", "Skyrim.exe" })]
    [InlineData(WabbajackArchiveFilter.NeedsYou, new[] { "failed.7z", "manual-b.7z", "browser-a.7z", "aaa-signin.7z" })]
    [InlineData(WabbajackArchiveFilter.Missing, new[] { "auto.7z", "failed.7z", "manual-b.7z", "browser-a.7z", "aaa-signin.7z" })]
    [InlineData(WabbajackArchiveFilter.InLibrary, new[] { "held.7z" })]
    public async Task EachFilter_ShowsItsRows_InTheListsOrder_AsTheSameRowObjects(WabbajackArchiveFilter filter, string[] expected)
    {
        var (vm, _, _) = await Open(Mixed());
        var archives = vm.Archives.ToArray();

        vm.ArchiveFilter = filter;

        Names(vm.VisibleArchives).Should().Equal(expected);
        vm.VisibleArchives.Should().OnlyContain(row => archives.Any(a => ReferenceEquals(a, row)));
        vm.Archives.Should().Equal(archives, "filtering changes what is shown, not the rows");
    }

    [Fact]
    public async Task TheDefaultFilter_ShowsEveryArchive_AsTheTablesOwnList()
    {
        var (vm, _, _) = await Open(Mixed());

        vm.ArchiveFilter.Should().Be(WabbajackArchiveFilter.All);
        vm.VisibleArchives.Should().BeSameAs(vm.Archives);
    }

    [Fact]
    public async Task UnderAFilter_ARowThatChanges_LeavesTheShownListInPlace()
    {
        var plan = Mixed();
        var (vm, _, acquirer) = await Open(plan);
        using var activation = vm.Activator.Activate();
        vm.ArchiveFilter = WabbajackArchiveFilter.Missing;
        var shown = vm.VisibleArchives;
        var archives = vm.Archives.ToArray();

        acquirer.Change(WithState(plan, plan.Rows.ToList().FindIndex(row => row.Archive.Name == "auto.7z"), ArchiveState.Have));

        vm.VisibleArchives.Should().BeSameAs(shown);
        Names(vm.VisibleArchives).Should().Equal("failed.7z", "manual-b.7z", "browser-a.7z", "aaa-signin.7z");
        vm.Archives.Should().Equal(archives);
    }

    [Fact]
    public async Task CopyList_PutsOneLinePerEntryOnTheClipboard()
    {
        var interop = Substitute.For<IAvaloniaInterop>();
        var (vm, _, _) = await Open(Mixed(), services: new OverridingServiceProvider(Provider, (typeof(IAvaloniaInterop), interop)));

        await vm.CopyNeedsYouCommand.Execute();

        await interop.Received(1).SetClipboardTextAsync(string.Join("\n",
            "aaa-signin.7z — https://www.loverslab.com/files/file/1-x/ — Sign in to LoversLab to download this",
            "browser-a.7z — no page — Needs a download in your browser",
            "manual-b.7z — https://beta.example/files/1 — hash mismatch",
            "failed.7z — no page — connection reset"));
    }

    [Fact]
    public async Task ShowThem_ScrollsToTheSection_WhenSomethingNeedsTheUser()
    {
        var (vm, _, _) = await Open(Mixed());
        var asked = 0;
        using var subscription = vm.NeedsYouRevealRequested.Subscribe(_ => asked++);

        vm.CanShowMissing.Should().BeTrue();
        await vm.ShowMissingCommand.Execute();

        asked.Should().Be(1);
        vm.ArchiveFilter.Should().Be(WabbajackArchiveFilter.All, "the table keeps what it shows");
    }

    [Fact]
    public async Task ShowThem_FiltersTheTableToWhatIsMissing_WhenNothingNeedsTheUser()
    {
        var (vm, _, _) = await Open(Plan(From("a.example", ArchiveState.Automatic, "auto.7z"), From("a.example", ArchiveState.Have, "held.7z")));
        var asked = 0;
        using var subscription = vm.NeedsYouRevealRequested.Subscribe(_ => asked++);

        await vm.ShowMissingCommand.Execute();

        asked.Should().Be(0);
        vm.ArchiveFilter.Should().Be(WabbajackArchiveFilter.Missing);
        Names(vm.VisibleArchives).Should().Equal("auto.7z");
    }

    [Fact]
    public async Task ShowThem_IsOffWhenEveryArchiveIsAvailable()
    {
        var (vm, _, _) = await Open(Plan(From("a.example", ArchiveState.Have, "held.7z"), From("a.example", ArchiveState.GameFile, "Skyrim.exe")));

        vm.CanShowMissing.Should().BeFalse();
    }

    private static Control[] SectionLines(WabbajackModlistView view) => view.FindControl<ItemsControl>("NeedsYouRows")!.GetVisualDescendants().OfType<WabbajackArchiveLine>().Cast<Control>().ToArray();

    [Fact]
    public async Task AHugeSection_RealizesOnlyTheLinesInView_AndACreditedRowLeavesTheOtherLinesAlone()
    {
        var plan = BigPlan(1153, ArchiveState.Browser);
        var (vm, _, acquirer) = await Open(plan);
        using var activation = vm.Activator.Activate();
        var view = await ShowView(vm);
        Control[] before = [];
        await EventuallyOnUi(() =>
        {
            Render((Window)view.GetVisualRoot()!);
            before = SectionLines(view);
            before.Should().NotBeEmpty().And.HaveCountLessThan(40);
        });

        acquirer.Change(WithState(plan, plan.Rows.ToList().FindIndex(row => row.Archive.Name == "archive-0.7z"), ArchiveState.Have));

        await EventuallyOnUi(() =>
        {
            Render((Window)view.GetVisualRoot()!);
            vm.NeedsYou.Should().HaveCount(1152);
            SectionLines(view).Should().Contain(before.Skip(1).Take(3), "the lines of the rows that stayed are not rebuilt");
        });
    }
}
