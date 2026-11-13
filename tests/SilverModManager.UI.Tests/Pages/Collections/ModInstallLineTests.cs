using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using System.Reactive.Concurrency;
using SilverModManager.App.UI;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.Sdk.Loadouts.Install;
using SilverModManager.UI.Tests.Framework;
using SilverModManager.UI.Tests.Helpers;

namespace SilverModManager.UI.Tests.Pages.Collections;

// Built is a process-wide counter, so this class runs with nothing else beside it.
[Collection(HeadlessUi.Collection)]
public class ModInstallLineTests(IServiceProvider provider) : AUiTest(provider)
{
    private static ModInstallRowState Waiting(int total = 5) => new(ModInstallState.Waiting, 0, 0, total);

    private static string Text(Grid line, string name) => line.Children.OfType<TextBlock>().Single(t => t.Name == name).Text ?? "";

    [Fact]
    public async Task TenThousandChangesOnAnAttachedLineQueueAtMostOneJob()
    {
        var scheduler = new QueuedScheduler();
        var row = new ModInstallRow("a", "Mod A", 0, Waiting(10_000));
        var line = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var made = new ModInstallLine(row, scheduler);
            Track(new Window { Width = 800, Height = 100, Content = made }).Show();
            Dispatcher.UIThread.RunJobs();
            return made;
        });

        for (var i = 1; i <= 10_000; i++)
        {
            row.Set(new ModInstallRowState(ModInstallState.Installing, i, i, 10_000));
            scheduler.Queue.Count.Should().BeLessThanOrEqualTo(1);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // A plain ObserveOn also keeps one job queued at a time, but drains the backlog one job per change.
            scheduler.RunAll().Should().Be(1, "only the latest state is worth a turn of the UI thread");
            Text(line, "ModState").Should().Be("installing");
            Text(line, "ModSummary").Should().Be("10,000 of 10,000 files");
        });
    }

    [Fact]
    public async Task ADetachedLineSchedulesNothing()
    {
        var scheduler = new QueuedScheduler();
        var row = new ModInstallRow("a", "Mod A", 0, Waiting());
        var never = await Dispatcher.UIThread.InvokeAsync(() => new ModInstallLine(row, scheduler));
        for (var i = 1; i <= 100; i++) row.Set(new ModInstallRowState(ModInstallState.Installing, i, i, 100));
        scheduler.Queue.Should().BeEmpty("a line that was never shown follows nothing");

        var window = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var w = Track(new Window { Width = 800, Height = 100, Content = new ModInstallLine(row, scheduler) });
            w.Show();
            Dispatcher.UIThread.RunJobs();
            return w;
        });
        await Dispatcher.UIThread.InvokeAsync(() => { window.Content = null; Dispatcher.UIThread.RunJobs(); });
        scheduler.Queue.Clear();

        for (var i = 1; i <= 100; i++) row.Set(new ModInstallRowState(ModInstallState.Installed, i, i, 100));
        scheduler.Queue.Should().BeEmpty("a line that left the tree stops following");
        never.Should().NotBeNull();
    }

    [Fact]
    public async Task ThreeThousandRowsBuildOnlyTheLinesInView()
    {
        var rows = new ModInstallRows();
        rows.Add(Enumerable.Range(0, 3000).Select(i => new ModInstallRow($"m{i}", $"Mod {i}", i, Waiting())));
        rows.SetTotals(new ModInstallTotals(3000, 0, 0));
        var before = ModInstallLine.Built;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // The page is one outer scroll viewer, which measures its children at infinite height.
            var section = new ModInstallRowsSection { Scheduler = ImmediateScheduler.Instance, Rows = rows };
            Track(new Window { Width = 800, Height = 400, Content = new ScrollViewer { Content = section } }).Show();
            Dispatcher.UIThread.RunJobs();
            (ModInstallLine.Built - before).Should().BeGreaterThan(0).And.BeLessThan(100);
        });
    }

    [Fact]
    public async Task TheFailedViewIsEmptyWhenNothingFailed()
    {
        var rows = new ModInstallRows();
        rows.Add(Enumerable.Range(0, 3).Select(i => new ModInstallRow($"m{i}", $"Mod {i}", i, new ModInstallRowState(ModInstallState.Installed, 5, 5, 5))));
        rows.SetTotals(new ModInstallTotals(3, 3, 0));

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var section = new ModInstallRowsSection { Scheduler = ImmediateScheduler.Instance, Rows = rows, Filter = ModInstallFilter.Failed };
            Track(new Window { Width = 800, Height = 400, Content = section }).Show();
            Dispatcher.UIThread.RunJobs();

            section.FindControl<TextBlock>("ModsHeader")!.Text.Should().Be("3 of 3 mods installed");
            section.FindControl<ItemsControl>("ModRows")!.ItemCount.Should().Be(0);

            section.Filter = ModInstallFilter.All;
            Dispatcher.UIThread.RunJobs();
            section.FindControl<ItemsControl>("ModRows")!.ItemCount.Should().Be(3);
        });
    }
}
