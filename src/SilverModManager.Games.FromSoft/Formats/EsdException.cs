namespace SilverModManager.Games.FromSoft.Formats;

/// <summary>An ESD was malformed, or used a layout the reader refuses to guess at.</summary>
public class EsdException(string message) : Exception(message);
