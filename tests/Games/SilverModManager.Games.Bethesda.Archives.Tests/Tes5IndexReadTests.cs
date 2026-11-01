using NexusMods.Paths;
using static SilverModManager.Games.Bethesda.Archives.Tests.TestArchives;

namespace SilverModManager.Games.Bethesda.Archives.Tests;

public class Tes5IndexReadTests
{
    /// <summary>A seekable stream that counts how many reads reach it.</summary>
    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int Reads { get; private set; }

        public override int Read(byte[] buffer, int offset, int count) { Reads++; return base.Read(buffer, offset, count); }

        public override int Read(Span<byte> buffer) { Reads++; return base.Read(buffer); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { Reads++; return base.ReadAsync(buffer, cancellationToken); }
    }

    [Fact]
    public async Task OpeningAnArchiveReadsItsIndexInAHandfulOfReadsHoweverManyFoldersItHolds()
    {
        var entries = Enumerable.Range(0, 300)
            .Select(i => new Tes5Entry($"meshes\\folder{i}", $"file{i}.nif", Pattern(10, (byte)i), false))
            .ToArray();
        var archive = Tes5(104, FolderAndFileNames, entries);
        await using var stream = new CountingStream(archive);

        await using var reader = await BethesdaArchive.Open(stream, CancellationToken.None);

        reader.Entries.Should().HaveCount(300);
        stream.Reads.Should().BeLessThan(8, "the index is contiguous, so one read covers it; 300 folders used to cost over 600 small reads");
    }

    [Fact]
    public async Task AnIndexReadInOneGoStillListsEveryMemberWithItsBytes()
    {
        var entries = Enumerable.Range(0, 50)
            .Select(i => new Tes5Entry($"textures\\f{i % 7}", $"t{i}.dds", Pattern(20 + i, (byte)i), false))
            .ToArray();
        await using var reader = await BethesdaArchive.Open(new MemoryStream(Tes5(104, FolderAndFileNames, entries)), CancellationToken.None);

        reader.Entries.Select(e => e.Path.ToString().Replace('\\', '/')).Should()
            .BeEquivalentTo(entries.Select(e => $"{e.Folder}/{e.Name}".Replace('\\', '/')));
    }
}
