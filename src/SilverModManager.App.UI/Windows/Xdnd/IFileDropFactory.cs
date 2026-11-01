namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>Turns the paths of a drag into the data object the window's drop handlers read. The session depends on this rather than on <see cref="FileDropData"/>, which needs a window's storage provider.</summary>
public interface IFileDropFactory
{
    Task<FileDrop> CreateAsync(IReadOnlyList<string> paths);
}
