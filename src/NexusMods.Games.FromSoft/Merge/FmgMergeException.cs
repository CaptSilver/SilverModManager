namespace NexusMods.Games.FromSoft.Merge;

/// <summary>Two message archives could not be merged without losing text, so the merge was refused.</summary>
public class FmgMergeException(string message) : Exception(message);
