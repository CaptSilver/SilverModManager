using System.Text.Json;
using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Mapping;

namespace SilverModManager.Wabbajack.Tests.Install;

public class ModGroupArchivesTests
{
    private static readonly FileIdentity Archive1 = Xx64.From(1);
    private static readonly FileIdentity Archive2 = Xx64.From(2);

    private static readonly ArchiveEntry Entry1 = Entry(Archive1, "a1.7z");
    private static readonly ArchiveEntry Entry2 = Entry(Archive2, "a2.7z");

    private static ArchiveEntry Entry(FileIdentity hash, string name)
        => new(hash, 1, name, "", new HttpState(new Uri($"https://example.invalid/{name}"), []));

    private static RelativePath P(string path) => RelativePath.FromUnsanitizedInput(path);

    private static FromArchive From(string to, FileIdentity archive) => new(P(to), Xx64.From(9), 1, new ArchiveHashPath(archive, [P("f.bin")]));

    private static InstallModlistJob.Placement Mod(string folder, Directive directive, int index)
        => new(index, directive, directive.To, new Mo2Target.ModFile(folder, new GamePath(LocationId.Game, directive.To)), folder, null);

    private static InstallModlistJob.Placement Root(string group, Directive directive, int index)
        => new(index, directive, directive.To, new Mo2Target.GameRootFile(group, directive.To), group, null);

    private static InstallModlistJob.Placement BsaInput(string tempId, FileIdentity archive, int index)
    {
        var directive = From($"TEMP_BSA_FILES/{tempId}/{index}.bin", archive);
        return new(index, directive, directive.To, new Mo2Target.BsaInput(P(tempId), P($"{index}.bin")), null, null);
    }

    private static CreateBsa Bsa(string to, string tempId) => new(P(to), Xx64.From(1), 1, P(tempId), JsonDocument.Parse("{}").RootElement, []);

    private static IReadOnlyDictionary<string, FileIdentity> Rule(
        InstallModlistJob.Placement[] placements,
        Dictionary<string, Dictionary<string, InstallModlistJob.Placement>>? bsaInputs = null,
        params ArchiveEntry[] archives)
        => ModGroupArchives.MainArchiveByFolder(placements, bsaInputs ?? new(), archives.Length == 0 ? [Entry1, Entry2] : archives);

    [Fact]
    public void AFolderBuiltFromOneArchiveNamesIt()
    {
        var result = Rule([Mod("A", From("mods/A/x.bin", Archive1), 0), Mod("A", From("mods/A/y.bin", Archive1), 1)]);

        result.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, FileIdentity>("A", Archive1));
    }

    [Fact]
    public void AFolderWithSeveralArchivesNamesTheOneMostOfItsFilesComeFrom()
    {
        var result = Rule([
            Mod("A", From("mods/A/1.bin", Archive2), 0),
            Mod("A", From("mods/A/2.bin", Archive2), 1),
            Mod("A", From("mods/A/3.bin", Archive2), 2),
            Mod("A", From("mods/A/4.bin", Archive1), 3),
        ]);

        result["A"].Should().Be(Archive2);
    }

    [Fact]
    public void ATieGoesToTheArchiveTheListNamesFirst()
    {
        InstallModlistJob.Placement[] placements = [
            Mod("A", From("mods/A/1.bin", Archive2), 0),
            Mod("A", From("mods/A/2.bin", Archive2), 1),
            Mod("A", From("mods/A/3.bin", Archive1), 2),
            Mod("A", From("mods/A/4.bin", Archive1), 3),
        ];

        Rule(placements, null, Entry1, Entry2)["A"].Should().Be(Archive1);
        Rule(placements, null, Entry2, Entry1)["A"].Should().Be(Archive2);
    }

    [Fact]
    public void AnArchiveTheListDoesNotNameLosesATie()
    {
        var result = Rule([Mod("A", From("mods/A/1.bin", Archive2), 0), Mod("A", From("mods/A/2.bin", Archive1), 1)], null, Entry1);

        result["A"].Should().Be(Archive1);
    }

    [Fact]
    public void ARebuiltArchiveCountsTheArchivesOfItsInputs()
    {
        var inputs = Enumerable.Range(10, 3).Select(i => BsaInput("t1", Archive2, i)).ToArray();
        var bsaInputs = new Dictionary<string, Dictionary<string, InstallModlistJob.Placement>>
        {
            ["t1"] = inputs.ToDictionary(p => ((Mo2Target.BsaInput)p.Target).Path.ToString(), p => p),
        };

        var result = Rule([Mod("A", From("mods/A/x.bin", Archive1), 0), Mod("A", Bsa("mods/A/a.bsa", "t1"), 1), .. inputs], bsaInputs);

        result["A"].Should().Be(Archive2);
    }

    [Fact]
    public void AFolderWithNoArchiveFilesHasNoMainArchive()
    {
        var inline = new InlineFile(P("mods/G/a.txt"), Xx64.From(5), 1, "blob");
        var merged = new MergedPatch(P("mods/G/b.txt"), Xx64.From(6), 1, "patch", []);

        Rule([Mod("G", inline, 0), Mod("G", merged, 1)]).Should().NotContainKey("G");
    }

    [Fact]
    public void OverwriteAndGameRootFoldersGetNoMainArchive()
    {
        var result = Rule([
            Mod(Mo2Layout.OverwriteGroup, From("overwrite/a.bin", Archive1), 0),
            Root("Game Folder Files", From("Game Folder Files/b.bin", Archive1), 1),
            Root("Stock Game", From("Stock Game/c.bin", Archive1), 2),
        ]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ARebuiltArchiveWithNoRecordedInputsCountsNothing()
    {
        var result = Rule([Mod("A", Bsa("mods/A/a.bsa", "missing"), 0)]);

        result.Should().NotContainKey("A");
    }

    [Fact]
    public void FoldersThatDifferOnlyByCaseAreOneFolder()
    {
        var result = Rule([Mod("SkyUI", From("mods/SkyUI/a.bin", Archive1), 0), Mod("skyui", From("mods/skyui/b.bin", Archive1), 1)]);

        result.Should().ContainSingle();
        result["SKYUI"].Should().Be(Archive1);
    }
}
