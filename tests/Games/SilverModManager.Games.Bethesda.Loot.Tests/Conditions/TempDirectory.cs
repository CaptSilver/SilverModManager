namespace SilverModManager.Games.Bethesda.Loot.Tests.Conditions;

/// <summary>A throwaway folder under the tests' scratch root.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(TestScratch.Root, "loot-conditions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Writes <paramref name="content"/> at <paramref name="relativePath"/>, creating the folders above it.</summary>
    public string Write(string relativePath, byte[]? content = null)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full) ?? Path);
        System.IO.File.WriteAllBytes(full, content ?? []);
        return full;
    }

    public string Write(string relativePath, string text) => Write(relativePath, System.Text.Encoding.UTF8.GetBytes(text));

    public string CreateDirectory(string relativePath)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
