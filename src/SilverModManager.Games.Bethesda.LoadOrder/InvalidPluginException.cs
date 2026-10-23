namespace NexusMods.Games.Bethesda.LoadOrder;

/// <summary>Thrown when a stream is not a readable plugin of the requested format.</summary>
public class InvalidPluginException : Exception
{
    public InvalidPluginException(string message) : base(message) { }
    public InvalidPluginException(string message, Exception inner) : base(message, inner) { }
}
