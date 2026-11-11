using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Games.TestFramework;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tests.Tools;

public class WineOutputHintsTests
{
    private readonly InMemoryFileSystem _fs = new(OSInformation.FakeUnix);
    private readonly GameInstallation _installation;
    private readonly ResolvedPrefix _steamPrefix;
    private readonly ResolvedPrefix _winePrefix;
    private readonly AbsolutePath _exe;

    public WineOutputHintsTests()
    {
        _installation = Installation();
        _steamPrefix = new ResolvedPrefix(_fs.FromUnsanitizedFullPath("/steam/compatdata/611670/pfx"), CompatibilityLayerKind.SteamProton, ProtonAppId: 611670);
        _winePrefix = new ResolvedPrefix(_fs.FromUnsanitizedFullPath("/wine"), CompatibilityLayerKind.PlainWine);
        _exe = _fs.FromUnsanitizedFullPath("/tools/Remap/Remap.exe");
    }

    private GameInstallation Installation()
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Skyrim VR");
        var gameFolder = _fs.FromUnsanitizedFullPath("/games/Skyrim VR");
        var result = new GameLocatorResult
        {
            Game = game,
            Path = gameFolder,
            Store = GameStore.Steam,
            StoreIdentifier = "611670",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
        };
        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, gameFolder)));
    }

    private static ToolRunResult Result(string strategy, string stderr = "", string stdout = "", string? fallback = null)
        => new(53, TimeSpan.FromSeconds(4), strategy, fallback, stdout, stderr);

    private static string ImportDll(string dll, string by = "a.exe")
        => $"0148:err:module:import_dll Library {dll} (which is needed by L\"Z:\\\\tools\\\\{by}\") not found\n";

    private string? Explain(ToolRunResult result, ResolvedPrefix? prefix = null, AbsolutePath? exe = null)
        => WineOutputHints.Explain(result, _installation, exe ?? _exe, prefix ?? _steamPrefix);

    [Fact]
    public void NamesTheMissingDllAndItsVerb()
        => Explain(Result("SteamProton", ImportDll("MSVBVM60.DLL"))).Should()
            .Contain("MSVBVM60.DLL").And.Contain("Visual Basic 6 runtime").And.Contain("protontricks 611670 -q vb6run");

    [Fact]
    public void AWinetricksCommandForANonSteamPrefix()
        => Explain(Result("PlainWine", ImportDll("MSVBVM60.DLL")), _winePrefix).Should()
            .Contain("WINEPREFIX=").And.Contain("winetricks -q vb6run");

    [Fact]
    public void ANullPrefixNamesTheDllWithoutACommand()
    {
        // Goes straight to WineOutputHints: the Explain helper turns a null prefix into the Steam one.
        var hint = WineOutputHints.Explain(Result("SteamProton", ImportDll("MSVBVM60.DLL")), _installation, _exe, null);
        hint.Should().Contain("MSVBVM60.DLL").And.Contain("Visual Basic 6 runtime");
        hint.Should().NotContain("-q").And.NotContain("protontricks").And.NotContain("winetricks");
    }

    [Fact]
    public void RoutesAnAppInstallableDllToThePrefixSetup()
    {
        var hint = Explain(Result("SteamProton", ImportDll("vcruntime140.dll")));
        hint.Should().Contain("Visual C++ 2015-2022 runtime").And.Contain("Health Check");
        hint.Should().NotContain("protontricks").And.NotContain("winetricks");
    }

    [Fact]
    public void AMixedCaseDllNameStillMatchesTheCatalogue()
        => Explain(Result("SteamProton", ImportDll("MsVbVm60.dll"))).Should().Contain("vb6run");

    [Fact]
    public void AnUnknownDllIsNamedWithoutAVerb()
        => Explain(Result("SteamProton", ImportDll("XINPUT1_3.dll"))).Should().Contain("XINPUT1_3.dll").And.NotContain("-q");

    [Fact]
    public void SeveralMissingDllsAreEachNamedOnce()
    {
        var stderr = ImportDll("MSVBVM60.DLL") + ImportDll("MSVBVM60.DLL", "b.exe") + ImportDll("MSVCR100.dll");
        var hint = Explain(Result("SteamProton", stderr))!;

        hint.Split("MSVBVM60.DLL").Should().HaveCount(2);
        hint.Split("MSVCR100.dll").Should().HaveCount(2);
    }

    [Fact]
    public void TheFlatpakTranslateMessageIsReadFromStdout()
    {
        const string message = "Could not translate the specified Unix filename to a DOS filename.\n";
        Explain(Result("Protontricks", stdout: message)).Should().Contain("flatpak override");
        Explain(Result("Protontricks", stderr: message)).Should().BeNull();
    }

    [Fact]
    public void TheFlatpakTranslateMessageIsOnlyExplainedForProtontricks()
        => Explain(Result("SteamProton", stdout: "Could not translate the specified Unix filename to a DOS filename.\n")).Should().BeNull();

    [Fact]
    public void TheFlatpakHintNamesTheToolsFolderAndTheFallbackReason()
    {
        var exe = _fs.FromUnsanitizedFullPath("/tools/Remap/x.exe");
        var result = Result("Protontricks", stdout: "Could not translate the specified Unix filename to a DOS filename.\n", fallback: "SteamProton cannot run: no Proton build");

        var hint = Explain(result, exe: exe);

        hint.Should().Contain("--filesystem='/tools/Remap'").And.Contain("com.github.Matoking.protontricks").And.Contain("SteamProton cannot run: no Proton build");
    }

    [Fact]
    public void AHostileToolFolderIsOneSingleQuotedWordInTheFlatpakHint()
    {
        var exe = _fs.FromUnsanitizedFullPath("""/tools/a $(touch x) `id` "q" 'single'/x.exe""");
        var result = Result("Protontricks", stdout: "Could not translate the specified Unix filename to a DOS filename.\n");

        Explain(result, exe: exe).Should().Contain("""--filesystem='/tools/a $(touch x) `id` "q" '\''single'\''' com.github""");
    }

    [Fact]
    public void AHostilePrefixPathIsQuotedInTheWinetricksHint()
    {
        var prefix = new ResolvedPrefix(_fs.FromUnsanitizedFullPath("""/p/$(touch x) `id` "q" 'single'"""), CompatibilityLayerKind.PlainWine);

        Explain(Result("PlainWine", ImportDll("MSVBVM60.DLL")), prefix).Should().Contain("""WINEPREFIX='/p/$(touch x) `id` "q" '\''single'\''' winetricks""");
    }

    [Fact]
    public void HidNoiseAloneExplainsNothing()
        => Explain(Result("SteamProton", "0024:err:hid:foo bar\n0024:err:hid:baz\n")).Should().BeNull();

    [Fact]
    public void SignificantPrefersErrLinesAndSkipsHid()
    {
        var lines = WineOutputHints.Significant("0024:err:hid:noise\n0148:err:module:x\n", "plain stdout\n0150:err:seh:y\n");

        lines.Should().Equal("0148:err:module:x", "0150:err:seh:y");
    }

    [Fact]
    public void SignificantFallsBackToTheLastRawLines()
    {
        var stderr = string.Concat(Enumerable.Range(1, 12).Select(i => $"raw {i}\n"));

        WineOutputHints.Significant(stderr, "").Should().Equal(Enumerable.Range(5, 8).Select(i => $"raw {i}"));
    }

    [Fact]
    public void SignificantIsCapped()
    {
        var stderr = string.Concat(Enumerable.Range(1, 12).Select(i => $"0148:err:module:e{i}\n"));

        WineOutputHints.Significant(stderr, "").Should().Equal(Enumerable.Range(5, 8).Select(i => $"0148:err:module:e{i}"));
    }
}
