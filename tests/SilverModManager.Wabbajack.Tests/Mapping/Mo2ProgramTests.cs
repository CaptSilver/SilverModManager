using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Mapping;
using SilverModManager.Wabbajack.Tests.Fixtures;

namespace SilverModManager.Wabbajack.Tests.Mapping;

public class Mo2ProgramTests
{
    private static readonly IReadOnlySet<string> Roots = new HashSet<string>(["Stock Game"], StringComparer.OrdinalIgnoreCase);

    private static RelativePath P(string path) => RelativePath.FromUnsanitizedInput(path);

    private static ArchiveEntry Archive(ulong id, string name)
        => new(Xx64.From(id), 10, name, "", new HttpState(new Uri("https://example.invalid/x"), []));

    private static FromArchive From(ArchiveEntry archive, string to)
        => new(P(to), Xx64.From(1), 1, new ArchiveHashPath(archive.XxHash64, [P("member")]));

    private static ModList List(ArchiveEntry[] archives, params Directive[] directives)
        => WabbajackFixture.EmptyList("t", directives) with { Archives = archives };

    [Theory]
    [InlineData("Mod.Organizer-2.4.4.7z")]
    [InlineData("Mod Organizer-2.5.0.zip")]
    [InlineData("ModOrganizer-2.4.4.7z")]
    [InlineData("mod.organizer-2.4.4.7z")]
    public void AnArchiveNamedLikeAModOrganizerReleaseWhoseFilesAreProgramFilesIsNotNeeded(string name)
    {
        var mo2 = Archive(1, name);
        var list = List([mo2], From(mo2, "ModOrganizer.exe"), From(mo2, "uibase.dll"), From(mo2, "plugins/basic_games/x.py"), From(mo2, "python/lib/os.py"));

        var found = Mo2Program.Find(list, Roots);

        found.NotNeeded.Should().BeEquivalentTo([mo2.XxHash64]);
        found.Kept.Should().BeEmpty();
    }

    [Fact]
    public void AnArchiveWithAnotherNameThatShipsModOrganizerExeAtTheRootIsRecognisedByItsFiles()
    {
        var odd = Archive(2, "MO2 portable build.7z");
        var list = List([odd], From(odd, "ModOrganizer.exe"), From(odd, "dlls/x.dll"));

        Mo2Program.Find(list, Roots).NotNeeded.Should().BeEquivalentTo([odd.XxHash64]);
    }

    [Fact]
    public void AnArchiveThatMerelyHasMoInItsNameIsNotRecognised()
    {
        var mod = Archive(3, "Mod Organizer 2 Skin Pack-1.0.7z");
        var list = List([mod], From(mod, "mods/Skin/a.dds"));

        var found = Mo2Program.Find(list, Roots);

        found.NotNeeded.Should().BeEmpty();
        found.Kept.Should().BeEmpty();
    }

    [Theory]
    [InlineData("mods/Some Mod/Data/a.esp")]
    [InlineData("overwrite/a.txt")]
    [InlineData("Stock Game/d3d11.dll")]
    [InlineData("stock game/d3d11.dll")]
    [InlineData("Game Folder Files/x.dll")]
    [InlineData("profiles/Default/modlist.txt")]
    [InlineData("TEMP_BSA_FILES/1/a.nif")]
    public void AFileTheListPlacesInAModOrTheGameKeepsTheArchiveAndSaysWhy(string to)
    {
        var mo2 = Archive(1, "Mod.Organizer-2.4.4.7z");
        var list = List([mo2], From(mo2, "ModOrganizer.exe"), From(mo2, to));

        var found = Mo2Program.Find(list, Roots);

        found.NotNeeded.Should().BeEmpty();
        found.Kept.Should().ContainKey(mo2.XxHash64).WhoseValue.Should().Contain("Mod Organizer 2").And.Contain(to);
    }

    [Theory]
    [InlineData("ModOrganizer.exe", true)]
    [InlineData("ModOrganizer.ini", true)]
    [InlineData("dlls/a.dll", true)]
    [InlineData("python/Lib/os.py", true)]
    [InlineData("tools/LOOT/loot.exe", true)]
    [InlineData("mods/A/a.esp", false)]
    [InlineData("Mods/A/a.esp", false)]
    [InlineData("overwrite/a.txt", false)]
    [InlineData("profiles/Default/plugins.txt", false)]
    [InlineData("TEMP_BSA_FILES/x/a.nif", false)]
    [InlineData("Game Folder Files/a.dll", false)]
    [InlineData("Stock Game/a.dll", false)]
    [InlineData("stock game/a.dll", false)]
    public void ProgramLocationsAreEverythingOutsideModsProfilesAndGameFolders(string to, bool program)
        => Mo2Program.IsProgramLocation(P(to), Roots).Should().Be(program);

    [Fact]
    public void ADirectiveThatIsNotFromAnArchiveNeverKeepsOne()
    {
        var mo2 = Archive(1, "Mod.Organizer-2.4.4.7z");
        var list = List([mo2], From(mo2, "ModOrganizer.exe"),
            new InlineFile(P("mods/A/a.txt"), Xx64.From(5), 1, "blob"));

        Mo2Program.Find(list, Roots).NotNeeded.Should().BeEquivalentTo([mo2.XxHash64]);
    }
}
