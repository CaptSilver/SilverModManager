namespace NexusMods.Games.FromSoft.Formats;

/// <summary>A regulation file could not be read or written: the game has none, or the bytes are not one.</summary>
public class RegulationException(string message) : Exception(message);
