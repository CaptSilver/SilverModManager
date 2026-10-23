using FluentAssertions;
using NexusMods.Games.FromSoft.Merge;
using NexusMods.Games.FromSoft.Tests.Formats;
using EsdFormat = NexusMods.Games.FromSoft.Formats.Esd;

namespace NexusMods.Games.FromSoft.Tests.Merge;

/// <summary>
/// The merged machine through this writer is the machine erm's merge writes through its own, byte for byte, and the
/// notes render to the sentences erm prints.
/// </summary>
public class EsdMergeGoldenTests
{
    public static TheoryData<string> Cases => [.. EsdMergeGoldenCases.All.Keys];

    [Theory, MemberData(nameof(Cases))]
    public void MergeWritesTheBytesErmWrites(string name)
    {
        var triple = EsdMergeGoldenCases.All[name]();
        var (merged, _) = EsdMerge.Merge(triple.Base, triple.Other, triple.Vanilla);
        EsdFormat.Write(merged).Should().Equal(GoldenFilesTests.Read($"esd_merge_{name}.esd"));
    }

    [Theory, MemberData(nameof(Cases))]
    public void NotesRenderToTheSentencesErmPrints(string name)
    {
        var triple = EsdMergeGoldenCases.All[name]();
        var (_, notes) = EsdMerge.Merge(triple.Base, triple.Other, triple.Vanilla);
        var expected = File.ReadAllLines(Path.Combine(GoldenFilesTests.Folder, $"esd_merge_{name}.notes.txt"));
        notes.Select(n => n.Describe()).Should().Equal(expected);
    }
}
