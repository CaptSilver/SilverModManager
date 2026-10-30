using System.Text.Json.Nodes;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Deployment.Vfs.Tests;

public class PromotionJournalTests : IDisposable
{
    private static readonly LoadoutId Target = LoadoutId.From(0x77);
    private static readonly GameInstallMetadataId Installation = GameInstallMetadataId.From(0xABC);

    private readonly AbsolutePath _directory = FileSystem.Shared
        .GetKnownPath(KnownPath.TempDirectory)
        .Combine($"vfs-journal-tests-{Guid.NewGuid():N}");

    public void Dispose() => _directory.DeleteDirectory(recursive: true);

    private AbsolutePath WriteWithoutField(JournalEntry entry, string field)
    {
        var file = _directory.Combine("journal.json");
        new PromotionJournal(Installation, Target, [entry]).Write(file);
        var json = JsonNode.Parse(File.ReadAllText(file.ToString()))!.AsObject();
        json["entries"]!.AsArray()[0]!.AsObject().Remove(field);
        File.WriteAllText(file.ToString(), json.ToJsonString());
        return file;
    }

    private static JournalEntry FileEntry() => new(
        RelativePath.FromUnsanitizedInput("Data/a.txt"), JournalEntryKind.File, Size.From(1),
        FileIdentity.XxHash3(Hash.From(0x1234UL)), 0x1a4);

    [Theory]
    [InlineData("path")]
    [InlineData("size")]
    [InlineData("xxHash3")]
    [InlineData("mode")]
    public void AFileEntryMissingAFieldNamesTheJournalTheEntryAndTheField(string field)
    {
        var file = WriteWithoutField(FileEntry(), field);

        var act = () => PromotionJournal.Read(file, Installation);

        act.Should().Throw<InvalidDataException>()
            .WithMessage($"*{file}*entry 0*file*'{field}'*");
    }

    [Fact]
    public void Read_rejects_a_null_entry()
    {
        var file = _directory.Combine("journal.json");
        new PromotionJournal(Installation, Target, [FileEntry()]).Write(file);
        var json = JsonNode.Parse(File.ReadAllText(file.ToString()))!.AsObject();
        json["entries"]!.AsArray()[0] = null;
        File.WriteAllText(file.ToString(), json.ToJsonString());

        var act = () => PromotionJournal.Read(file, Installation);

        act.Should().Throw<InvalidDataException>().WithMessage("*entry 0*");
    }

    [Fact]
    public void AWhiteoutEntryMissingItsPathNamesTheJournalTheEntryAndTheField()
    {
        var whiteout = new JournalEntry(RelativePath.FromUnsanitizedInput("Data/gone.txt"), JournalEntryKind.Whiteout, Size.Zero, default, 0);
        var file = WriteWithoutField(whiteout, "path");

        var act = () => PromotionJournal.Read(file, Installation);

        act.Should().Throw<InvalidDataException>()
            .WithMessage($"*{file}*entry 0*whiteout*'path'*");
    }

    [Fact]
    public void ACompleteJournalReadsBack()
    {
        var file = _directory.Combine("journal.json");
        var entry = FileEntry();
        new PromotionJournal(Installation, Target, [entry]).Write(file);

        var read = PromotionJournal.Read(file, Installation);

        read.Entries.Should().ContainSingle().Which.Should().Be(entry);
    }
}
