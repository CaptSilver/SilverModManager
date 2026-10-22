namespace NexusMods.Games.FromSoft.Formats;

/// <summary>A TPF was malformed, or used a layout the writer cannot express.</summary>
public class TpfException(string message) : Exception(message);
