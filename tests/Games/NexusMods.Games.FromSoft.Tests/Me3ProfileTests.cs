using FluentAssertions;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.Paths;

namespace NexusMods.Games.FromSoft.Tests;

public class Me3ProfileTests
{
    private static readonly IFileSystem FileSystem = new InMemoryFileSystem();
    private static AbsolutePath Mods(string root = "/data/EldenRing/1245620/mods") => FileSystem.FromUnsanitizedFullPath(root);
    private static AbsolutePath GameFolder(string root = "/steam/ELDEN RING/Game") => FileSystem.FromUnsanitizedFullPath(root);

    private const string ErscDll = "overlay/SeamlessCoop/ersc.dll";
    private const string ErscIni = "overlay/SeamlessCoop/ersc_settings.ini";

    private static readonly Me3ProfileSpec EldenRingSpec = new("eldenring", EldenRingPaths.ModdedSave, [SeamlessCoop.Native]);

    private static string Render(params string[] files) => Me3Profile.Render(EldenRingSpec, files, Mods(), GameFolder());

    [Fact]
    public void TheHeaderNamesTheProfileVersionAndTheGame()
    {
        var text = Render();

        text.Should().StartWith("profileVersion = \"v1\"\n");
        text.Should().Contain("[[supports]]\ngame = \"eldenring\"\n");
    }

    [Fact]
    public void AnEmptyLoadoutHasNoNativesPackagesOrSaveFile()
    {
        var text = Render();

        text.Should().NotContain("[[natives]]").And.NotContain("[[packages]]").And.NotContain("savefile");
    }

    [Fact]
    public void TheSeamlessCoopNativeIsListedOnlyWhenItsDllIsInTheLoadout()
    {
        Render("Weapons/parts/wp_a.dcx").Should().NotContain("[[natives]]");

        var text = Render(ErscDll, ErscIni);

        text.Should().Contain("[[natives]]\npath = '/steam/ELDEN RING/Game/SeamlessCoop/ersc.dll'\n");
    }

    [Fact]
    public void ThePackagesAreSortedByIdWhateverOrderTheFilesComeIn()
    {
        var text = Render("zebra/parts/z.dcx", "alpha/parts/a.dcx", "Mid/parts/m.dcx");

        var ids = Parse(text).Packages.Select(p => p.Id).ToArray();

        ids.Should().Equal("Mid", "alpha", "zebra");
        text.Should().Contain("id = \"alpha\"\npath = '/data/EldenRing/1245620/mods/alpha/'\n");
    }

    [Fact]
    public void APackageWithSeveralFilesIsListedOnce()
    {
        var text = Render("alpha/parts/a.dcx", "alpha/menu/b.dds", "alpha/regulation.bin");

        Parse(text).Packages.Should().ContainSingle();
    }

    [Fact]
    public void TheFolderMeaningsAndTheProfileItselfAreNotPackages()
    {
        var text = Render("silver.me3", ErscDll, ErscIni, "natives/Hook/hook.dll", "alpha/parts/a.dcx");

        Parse(text).Packages.Select(p => p.Id).Should().Equal("alpha");
    }

    [Fact]
    public void NativesFollowTheSeamlessCoopOneAndThenSortByPackageId()
    {
        var text = Render("natives/zeta/zeta.dll", "natives/Alpha/Alpha.dll", ErscDll);

        Parse(text).NativePaths.Should().Equal(
            "/steam/ELDEN RING/Game/SeamlessCoop/ersc.dll",
            "/data/EldenRing/1245620/mods/natives/Alpha/Alpha.dll",
            "/data/EldenRing/1245620/mods/natives/zeta/zeta.dll");
    }

    [Fact]
    public void ANativeIsTheDllTheInstallerWouldHaveChosen()
    {
        var text = Render("natives/Hook/lib/dep.dll", "natives/Hook/lib/hook.dll", "natives/Hook/readme.txt");

        Parse(text).NativePaths.Should().Equal("/data/EldenRing/1245620/mods/natives/Hook/lib/hook.dll");
    }

    [Fact]
    public void ANativesFolderWithNoSingleDllToLoadIsRefusedByName()
    {
        var render = () => Render("natives/Hook/a.dll", "natives/Hook/b.dll");

        render.Should().Throw<InvalidOperationException>().WithMessage("*Hook*a.dll*b.dll*");
    }

    [Fact]
    public void TheSameStateGivesTheSameTextWhateverOrderTheFilesWereInstalledIn()
    {
        string[] state = ["natives/N/n.dll", "b/parts/b.dcx", ErscDll, ErscIni, "a/menu/a.dds"];

        var first = Render(state);
        var reversed = Render(state.Reverse().ToArray());
        var reinstalled = Render(state.Where(f => f != "a/menu/a.dds").Append("a/menu/a.dds").ToArray());

        reversed.Should().Be(first);
        reinstalled.Should().Be(first);
    }

    [Fact]
    public void ARemovedPackageLeavesNoTraceInTheProfile()
    {
        var with = Render("a/parts/a.dcx", "b/parts/b.dcx");
        var without = Render("a/parts/a.dcx");

        with.Should().Contain("\"b\"");
        without.Should().NotContain("\"b\"");
        Parse(without).Packages.Select(p => p.Id).Should().Equal("a");
    }

    [Fact]
    public void APathWithAnApostropheIsWrittenAsAnEscapedBasicString()
    {
        var text = Me3Profile.Render(EldenRingSpec, ["natives/N/n.dll", ErscDll], Mods("/data/O'Brien/mods"), GameFolder("/steam/O'Brien/Game"));

        text.Should().Contain("path = \"/steam/O'Brien/Game/SeamlessCoop/ersc.dll\"");
        Parse(text).NativePaths.Should().Equal("/steam/O'Brien/Game/SeamlessCoop/ersc.dll", "/data/O'Brien/mods/natives/N/n.dll");
    }

    [Fact]
    public void ABackslashOrQuoteInAPackageIdIsEscaped()
    {
        var text = Render("say \"hi\"/parts/a.dcx");

        text.Should().Contain("id = \"say \\\"hi\\\"\"");
        Parse(text).Packages.Single().Id.Should().Be("say \"hi\"");
    }

    [Fact]
    public void APlainPathIsWrittenAsALiteralString()
    {
        Render("a/parts/a.dcx").Should().Contain("path = '/data/EldenRing/1245620/mods/a/'");
    }

    [Fact]
    public void TheOutputEndsWithOneNewlineAndNothingFollowsTheLastBlock()
    {
        var text = Render("a/parts/a.dcx");

        text.Should().EndWith("path = '/data/EldenRing/1245620/mods/a/'\n");
    }

    // The save file key and its placement are a guess that has not been checked against me3 0.13: nothing in the
    // existing tooling writes the key. Both live in one constant each, so a live check changes one line.
    [Fact]
    public void ASaveFileIsPointedAtForAModdedLoadoutWithoutSeamlessCoop()
    {
        var text = Render("a/parts/a.dcx");

        text.Split('\n')[1].Should().Be($"{Me3Profile.SaveFileKey} = \"{EldenRingPaths.ModdedSave}\"");
    }

    [Fact]
    public void ANativeAloneAlsoCountsAsModded()
    {
        Render("natives/N/n.dll").Should().Contain(Me3Profile.SaveFileKey);
    }

    [Fact]
    public void NoSaveFileIsWrittenWithSeamlessCoopBecauseItsSaveExtensionSeparatesTheSaves()
    {
        Render("a/parts/a.dcx", ErscDll).Should().NotContain(Me3Profile.SaveFileKey);
    }

    [Fact]
    public void NoSaveFileIsWrittenForALoadoutWithNothingInstalled()
    {
        Render().Should().NotContain(Me3Profile.SaveFileKey);
        Render(ErscIni).Should().NotContain(Me3Profile.SaveFileKey);
    }

    [Fact]
    public void ThePackageTableKeyIsOneConstant()
    {
        var text = Render("a/parts/a.dcx");

        text.Should().Contain($"[[{Me3Profile.PackageTableKey}]]");
    }

    [Fact]
    public void AnotherGamesSpecNamesItsGameAndSaveAndNeverTheSeamlessCoopDll()
    {
        var spec = new Me3ProfileSpec("testsouls", "TS0000_silver.sl2", []);

        var text = Me3Profile.Render(spec, ["natives/N/n.dll", ErscDll], Mods(), GameFolder());

        text.Should().Contain("[[supports]]\ngame = \"testsouls\"\n");
        text.Should().Contain("savefile = \"TS0000_silver.sl2\"\n");
        text.Should().NotContain("ersc.dll");
    }

    [Fact]
    public void ASpecWithoutAModdedSaveWritesNoSaveFileLineEvenWhenModded()
    {
        var spec = new Me3ProfileSpec("testsouls", null, []);

        var text = Me3Profile.Render(spec, ["a/parts/a.dcx", "natives/N/n.dll"], Mods(), GameFolder());

        text.Should().NotContain(Me3Profile.SaveFileKey);
    }

    private sealed record Parsed(string[] NativePaths, (string Id, string Path)[] Packages);

    /// <summary>
    /// Reads back the two kinds of block the profile writes, so the tests check what a TOML reader would see and not
    /// the quoting. Handles only the two string forms the writer produces.
    /// </summary>
    private static Parsed Parse(string text)
    {
        var natives = new List<string>();
        var packages = new List<(string Id, string Path)>();
        string? table = null;
        string? id = null;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("[["))
            {
                table = line.Trim('[', ']');
                id = null;
            }
            else if (line.StartsWith("id = ")) id = ReadString(line["id = ".Length..]);
            else if (line.StartsWith("path = "))
            {
                var path = ReadString(line["path = ".Length..]);
                if (table == "natives") natives.Add(path);
                else if (table == Me3Profile.PackageTableKey) packages.Add((id!, path));
            }
        }

        return new Parsed(natives.ToArray(), packages.ToArray());
    }

    private static string ReadString(string value)
        => value[0] == '\'' ? value.Trim('\'') : value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
}
