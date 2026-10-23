namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// Two files could not be merged without losing content, so the merge was refused. The message gives the reason
/// without a path; the caller that knows which file it was working on adds that.
/// </summary>
public class MergeException(string message) : Exception(message);
