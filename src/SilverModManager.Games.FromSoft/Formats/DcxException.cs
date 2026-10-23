namespace SilverModManager.Games.FromSoft.Formats;

/// <summary>A DCX container was malformed, used a compression the game does not ship, or was asked for in a form that cannot be written.</summary>
public class DcxException(string message) : Exception(message);
