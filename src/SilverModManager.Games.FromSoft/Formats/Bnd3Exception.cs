namespace NexusMods.Games.FromSoft.Formats;

/// <summary>A BND3 archive was malformed, or a rebuild asked for something a structural clone cannot express.</summary>
public class Bnd3Exception(string message) : Exception(message);
