using FluentAssertions;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.App.UI.X11.Tests;

/// <summary>
/// The receiver against real Avalonia X11 windows on a private Xvfb display. A synthetic source speaks XDND to the
/// test host's window, so these cover what the fakes in SilverModManager.UI.Tests stand in for: the libX11 requests, the
/// tap on Avalonia's event handler, and the messages on the wire.
/// </summary>
public class XdndXvfbTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly string[] DragKinds = ["DragEnter", "DragOver", "DragLeave", "Drop"];

    [Fact]
    public async Task TwoFilesDroppedFromASourceReachTheWindow()
    {
        await using var rig = await XvfbRig.StartAsync();
        var files = CreateFiles(rig, 2);
        using var source = new XdndSyntheticSource(rig.DisplayName, SourceOffer.Of(files));

        var script = new DragScript(rig, source).RunToDrop();

        var reports = (await rig.WaitForAsync("Drop", Deadline)).Where(r => DragKinds.Contains(r.Kind)).ToList();
        reports.Select(r => r.Kind).Should().Equal("DragEnter", "DragOver", "Drop");
        reports[^1].Paths.Should().Equal(files);
        script.FirstStatus.Should().Be(new StatusReply(true, true, source.CopyAction));
        script.SecondStatus!.Accepted.Should().BeTrue();
        script.Finished.Should().Be(new FinishedReply(true, source.CopyAction));
    }

    [Fact]
    public async Task ALargeListArrivesOverIncr()
    {
        await using var rig = await XvfbRig.StartAsync();
        var files = CreateFiles(rig, 800);
        var offer = SourceOffer.Of(files, incrChunkSize: 16 * 1024);
        offer.UriList.Length.Should().BeGreaterThan(100 * 1024, "the list has to be big enough to need several chunks");
        using var source = new XdndSyntheticSource(rig.DisplayName, offer);

        var script = new DragScript(rig, source).RunToDrop();

        var reports = await rig.WaitForAsync("Drop", Deadline);
        reports.Single(r => r.Kind == "Drop").Paths.Should().Equal(files);
        script.Finished!.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task ASourceThatRefusesEveryConversionIsAskedAgainThenAnsweredWithARefusal()
    {
        await using var rig = await XvfbRig.StartAsync();
        using var source = new XdndSyntheticSource(rig.DisplayName, SourceOffer.Refusing());
        var xid = rig.WindowXid;

        source.Enter(xid);
        source.Position(xid, rig.Ready.X, rig.Ready.Y);
        var status = source.WaitStatus(Deadline);
        source.Drop(xid);
        var finished = source.WaitFinished(Deadline);
        source.PumpFor(TimeSpan.FromMilliseconds(300));

        status.Accepted.Should().BeFalse();
        source.ConversionRequests.Should().BeGreaterThan(2, "a refused conversion is retried every RetryDelay while the Status is owed");
        finished.Accepted.Should().BeFalse();
        rig.TakeReports().Where(r => DragKinds.Contains(r.Kind)).Should().BeEmpty("a drag whose data never came never reaches the window");
    }

    [Fact]
    public async Task AStatusSentToADestroyedSourceLeavesTheReceiverServingDrags()
    {
        await using var rig = await XvfbRig.StartAsync();
        var files = CreateFiles(rig, 1);
        var xid = rig.WindowXid;

        // Three types inline, so the receiver doesn't read XdndTypeList from the window that is about to vanish.
        // With the owner gone the server refuses every conversion, and at DataTimeout the Status goes to a dead window.
        using (var vanishing = new XdndSyntheticSource(rig.DisplayName, SourceOffer.Of(files)))
        {
            vanishing.Enter(xid, typesInProperty: false);
            vanishing.Position(xid, rig.Ready.X, rig.Ready.Y);
        }
        await Task.Delay(XdndSession.DataTimeout + TimeSpan.FromMilliseconds(500));

        rig.HostIsRunning.Should().BeTrue("the X error from the dead window is absorbed by Avalonia's handler");
        rig.TakeReports().Where(r => DragKinds.Contains(r.Kind)).Should().BeEmpty();

        using var source = new XdndSyntheticSource(rig.DisplayName, SourceOffer.Of(files));
        new DragScript(rig, source).RunToDrop();
        var reports = await rig.WaitForAsync("Drop", Deadline);
        reports.Single(r => r.Kind == "Drop").Paths.Should().Equal(files);
    }

    /// <summary>Real files, because the receiver opens each dropped path through the window's storage provider and leaves out those that don't exist. Long names make 800 of them a list of over 100 KiB.</summary>
    private static string[] CreateFiles(XvfbRig rig, int count)
    {
        var dir = Directory.CreateDirectory(Path.Combine(rig.Scratch, "dropped")).FullName;
        var padding = new string('x', 90);
        return Enumerable.Range(0, count)
            .Select(i =>
            {
                var path = Path.Combine(dir, $"{i:D4}-{padding}.zip");
                File.WriteAllBytes(path, [1]);
                return path;
            })
            .ToArray();
    }
}
