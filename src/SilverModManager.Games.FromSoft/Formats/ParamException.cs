namespace NexusMods.Games.FromSoft.Formats;

/// <summary>A PARAM was malformed, used a layout the reader refuses to guess at, or an edit would produce a broken one.</summary>
public class ParamException(string message) : Exception(message);
