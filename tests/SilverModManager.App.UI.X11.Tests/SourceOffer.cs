using System.Text;

namespace SilverModManager.App.UI.X11.Tests;

/// <summary>What a synthetic drag source does when the target asks for <c>text/uri-list</c>.</summary>
/// <param name="UriList">The bytes written to the requestor's property.</param>
/// <param name="IncrChunkSize">When set, the data goes over the INCR protocol in chunks of this many bytes.</param>
/// <param name="RefuseConversions">Answer every request with property None, as KWin does while the active window isn't an X11 one.</param>
internal sealed record SourceOffer(byte[] UriList, int? IncrChunkSize = null, bool RefuseConversions = false)
{
    public static SourceOffer Of(IEnumerable<string> paths, int? incrChunkSize = null) =>
        new(Encoding.UTF8.GetBytes(string.Concat(paths.Select(p => new Uri(p).AbsoluteUri + "\r\n"))), incrChunkSize);

    public static SourceOffer Refusing() => new([], RefuseConversions: true);
}

/// <summary>An XdndStatus as the target sent it.</summary>
internal sealed record StatusReply(bool Accepted, bool WantsContinuousPositions, nint Action);

/// <summary>An XdndFinished as the target sent it.</summary>
internal sealed record FinishedReply(bool Accepted, nint Action);
