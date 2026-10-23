namespace SilverModManager.Backend.FileExtractor;

/// <inheritdoc />
public class FileExtractionException : Exception
{
    /// <inheritdoc />
    public FileExtractionException(string message) : base(message) { }
}
