namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>The atoms the receiver talks in, interned once per transport.</summary>
public sealed class XdndAtoms
{
    public XdndAtoms(IXTransport x)
    {
        XdndAware = x.InternAtom("XdndAware");
        XdndEnter = x.InternAtom("XdndEnter");
        XdndPosition = x.InternAtom("XdndPosition");
        XdndStatus = x.InternAtom("XdndStatus");
        XdndLeave = x.InternAtom("XdndLeave");
        XdndDrop = x.InternAtom("XdndDrop");
        XdndFinished = x.InternAtom("XdndFinished");
        XdndTypeList = x.InternAtom("XdndTypeList");
        XdndSelection = x.InternAtom("XdndSelection");
        XdndActionCopy = x.InternAtom("XdndActionCopy");
        XdndActionMove = x.InternAtom("XdndActionMove");
        XdndActionLink = x.InternAtom("XdndActionLink");
        XdndActionAsk = x.InternAtom("XdndActionAsk");
        XdndActionPrivate = x.InternAtom("XdndActionPrivate");
        TextUriList = x.InternAtom("text/uri-list");
        Incr = x.InternAtom("INCR");
        DataProperties = [x.InternAtom("SilverModManager_XDND_0"), x.InternAtom("SilverModManager_XDND_1")];
    }

    public nint XdndAware { get; }
    public nint XdndEnter { get; }
    public nint XdndPosition { get; }
    public nint XdndStatus { get; }
    public nint XdndLeave { get; }
    public nint XdndDrop { get; }
    public nint XdndFinished { get; }
    public nint XdndTypeList { get; }
    public nint XdndSelection { get; }
    public nint XdndActionCopy { get; }
    public nint XdndActionMove { get; }
    public nint XdndActionLink { get; }
    public nint XdndActionAsk { get; }
    public nint XdndActionPrivate { get; }
    public nint TextUriList { get; }
    public nint Incr { get; }

    /// <summary>"SilverModManager_XDND_0" and "_1": the session alternates them per conversion request, because a
    /// SelectionNotify carries no request id.</summary>
    public IReadOnlyList<nint> DataProperties { get; }
}
