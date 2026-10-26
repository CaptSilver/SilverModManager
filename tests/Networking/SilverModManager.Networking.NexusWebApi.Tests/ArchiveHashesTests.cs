using FluentAssertions;

namespace SilverModManager.Networking.NexusWebApi.Tests;

public class ArchiveHashesTests
{
    [Fact]
    public void HashesAreLowercasedDeduplicatedAndSplitIntoBatches()
    {
        var batches = ArchiveHashes.Batch(["AA", "bb", "aa", "cc", "dd"], 2);

        batches.Should().BeEquivalentTo(new[] { new[] { "aa", "bb" }, new[] { "cc", "dd" } }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void NothingToLookUpGivesNoBatch()
    {
        ArchiveHashes.Batch([], 10).Should().BeEmpty();
    }

    [Theory]
    [InlineData("mod_file", true)]
    [InlineData("MOD_FILE", true)]
    [InlineData("collection_revision", false)]
    [InlineData("", false)]
    public void OnlyAnUploadedModArchiveCounts(string fileType, bool counts)
    {
        ArchiveHashes.IsModArchive(fileType).Should().Be(counts);
    }
}
