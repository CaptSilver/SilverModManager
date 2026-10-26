using SilverModManager.Games.FromSoft.Formats;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// Tells a failure caused by the mods' files from a bug in the app.
/// </summary>
internal static class MergeFailures
{
    /// <summary>
    /// Whether <paramref name="exception"/> says a path cannot be merged: a strategy refused with its own exception,
    /// or a reader under it found a file that is not what its format needs. These become a refusal the player can
    /// act on; anything else (a missing native library, an index out of range in a parser) is a bug and has to
    /// reach the crash report instead of being worded as "can't merge".
    /// </summary>
    internal static bool IsInputProblem(Exception exception) => exception is
        MergeException or ParamException or TpfException or EsdMergeException or EsdException or FmgException
        or DcxException or RegulationException or Bnd3Exception or Bnd4Exception or Bhd5Exception or AesException
        or KrakenException;
}
