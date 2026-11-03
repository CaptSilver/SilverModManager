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
    /// <summary>Versions keyed by full path, for files of the same name in different folders; they win over <see cref="Versions"/>.</summary>
    public Dictionary<string, Version> VersionsByPath { get; } = new();
    /// <summary>Names of the images that are 32-bit; every other image is 64-bit.</summary>
    public HashSet<string> ThirtyTwoBit { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ImportReads { get; } = [];

    public NativeImageImports ReadImports(AbsolutePath file)
    {
        ImportReads.Add(file.FileName);
        return new NativeImageImports(Imports.GetValueOrDefault(file.FileName) ?? [], Is64Bit: !ThirtyTwoBit.Contains(file.FileName));
    }

    public Version? FileVersion(AbsolutePath file)
        => VersionsByPath.GetValueOrDefault(file.GetFullPath()) ?? Versions.GetValueOrDefault(file.FileName);
}
