using NexusMods.Paths;
using SilverModManager.Games.Generic.Diagnostics;

namespace SilverModManager.Games.Generic.Tests;

/// <summary>
/// Answers from tables keyed by file name, so the emitter tests need no real PE files.
/// </summary>
internal sealed class FakeNativeImageReader : INativeImageReader
{
    public Dictionary<string, string[]> Imports { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Version> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ImportReads { get; } = [];

    public IReadOnlyList<string> ImportedLibraries(AbsolutePath file)
    {
        ImportReads.Add(file.FileName);
        return Imports.GetValueOrDefault(file.FileName) ?? [];
    }

    public Version? FileVersion(AbsolutePath file) => Versions.GetValueOrDefault(file.FileName);
}
