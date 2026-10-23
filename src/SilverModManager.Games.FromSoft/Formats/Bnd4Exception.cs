namespace NexusMods.Games.FromSoft.Formats;

/// <summary>A BND4 archive was malformed, or a rebuild asked for something a structural clone cannot express.</summary>
public class Bnd4Exception(string message) : Exception(message);
