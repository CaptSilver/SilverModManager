namespace SilverModManager.Backend.FileExtractor;

/// <inheritdoc />
public class FileExtractionException : Exception
{
    /// <inheritdoc />
    public FileExtractionException(string message) : base(message) { }

    /// <inheritdoc />
    public FileExtractionException(string message, Exception? innerException) : base(message, innerException) { }
}
