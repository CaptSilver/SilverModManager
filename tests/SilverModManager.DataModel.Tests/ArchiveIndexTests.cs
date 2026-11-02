using System.Collections.Frozen;
using FluentAssertions;
using NexusMods.Archives.Nx.Headers.Managed;
using NexusMods.Archives.Nx.Structs;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using Xunit;

namespace SilverModManager.DataModel.Tests;

public class ArchiveIndexTests
{
    private static ArchiveContents Archive(string name, params ulong[] hashes)
        => new(FileSystem.Shared.FromUnsanitizedFullPath($"/tmp/archive-index-tests/{name}.nx"), Size.FromLong(1), DateTimeOffset.UnixEpoch,
            hashes.ToFrozenDictionary(h => Hash.From(h), _ => default(FileEntry)));

    [Fact]
    public void AnArchiveAddedToAnIndexIsFoundByItsPathAndByEachFileInIt()
    {
        var added = Archive("a", 1, 2);

        var index = ArchiveIndex.Empty.With(added);

        index.TryGetArchive(added.ArchivePath, out var byPath).Should().BeTrue();
        byPath.Should().BeSameAs(added);
        index.TryGetArchiveOf(Hash.From(2), out var byFile).Should().BeTrue();
        byFile.Should().BeSameAs(added);
        index.ContainsFile(Hash.From(3)).Should().BeFalse();
    }

    [Fact]
    public void AddingToAnIndexLeavesTheOneItWasAddedToUnchanged()
    {
        var before = ArchiveIndex.Of([Archive("a", 1)]);

        before.With(Archive("b", 2));

        before.ContainsFile(Hash.From(2)).Should().BeFalse();
        before.ContainsFile(Hash.From(1)).Should().BeTrue();
    }

    [Fact]
    public void AFileInTwoArchivesIsAttributedToTheOneAddedLast()
    {
        var first = Archive("first", 7);
        var second = Archive("second", 7);

        var index = ArchiveIndex.Of([first]).With(second);

        index.TryGetArchiveOf(Hash.From(7), out var archive).Should().BeTrue();
        archive.Should().BeSameAs(second);
    }

    [Fact]
    public void NothingIsLostAcrossTheAdditionsThatFoldTheRecentArchivesIntoTheBulk()
    {
        // Enough archives of enough files to fold more than once.
        var index = ArchiveIndex.Empty;
        var archives = new List<ArchiveContents>();
        for (var a = 0; a < 200; a++)
        {
            var archive = Archive($"bulk{a}", Enumerable.Range(0, 1000).Select(i => (ulong)(a * 1000 + i)).ToArray());
            archives.Add(archive);
            index = index.With(archive);
        }

        foreach (var archive in archives)
        {
            index.TryGetArchive(archive.ArchivePath, out var found).Should().BeTrue();
            found.Should().BeSameAs(archive);
        }
        foreach (var hash in new ulong[] { 0, 31_999, 32_000, 99_999, 100_000, 199_999 })
        {
            index.TryGetArchiveOf(Hash.From(hash), out var found).Should().BeTrue();
            found.Entries.ContainsKey(Hash.From(hash)).Should().BeTrue();
        }
        index.ContainsFile(Hash.From(200_000)).Should().BeFalse();
    }

    [Fact]
    public void ReplacingAnArchiveAtTheSamePathReplacesItWhereverTheOldOneSits()
    {
        var old = Archive("same", 1);
        var replacement = Archive("same", 2);

        var index = ArchiveIndex.Of([old]).With(replacement);

        index.TryGetArchive(old.ArchivePath, out var found).Should().BeTrue();
        found.Should().BeSameAs(replacement);
    }
}
