namespace SilverModManager.App.UI.X11.Tests;

/// <summary>The messages of one complete drag, as a source that behaves like KWin's bridge sends them.</summary>
internal sealed class DragScript(XvfbRig rig, XdndSyntheticSource source)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    public StatusReply? FirstStatus { get; private set; }
    public StatusReply? SecondStatus { get; private set; }
    public FinishedReply? Finished { get; private set; }

    /// <summary>
    /// Enter, a Position on the border, a second Position a pixel over (the window sees Enter then Over), then Drop.
    /// Each Position waits for the Status the target owes before the next message, as KWin does.
    /// </summary>
    public DragScript RunToDrop()
    {
        var xid = rig.WindowXid;
        source.Enter(xid);
        source.Position(xid, rig.Ready.X, rig.Ready.Y);
        FirstStatus = source.WaitStatus(Deadline);

        source.Position(xid, rig.Ready.X + 1, rig.Ready.Y);
        SecondStatus = source.WaitStatus(Deadline);

        source.Drop(xid);
        Finished = source.WaitFinished(Deadline);
        return this;
    }
}
