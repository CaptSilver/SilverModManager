namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>A state machine could not be compared or merged without guessing, so it was refused.</summary>
public class EsdMergeException(string message) : Exception(message);
