namespace NexusMods.Games.FromSoft.Formats;

/// <summary>An FMG text table was malformed or used an unsupported version.</summary>
public class FmgException(string message) : Exception(message);
