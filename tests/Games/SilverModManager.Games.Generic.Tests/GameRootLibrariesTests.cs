using System.Collections.Immutable;
using FluentAssertions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Games.Generic.Diagnostics;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.Generic.Tests;

/// <summary>
/// Which DLLs in a game's folder the game loads by name, and which Wine would shadow with its own copy. The
/// prefix is a temp folder with the files the test lists in <c>system32</c>; no real prefix is read.
/// </summary>
public sealed class GameRootLibrariesTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(TestScratch.Combine("game-root-libraries", Guid.NewGuid().ToString("N"))).FullName;
    private readonly FakeNativeImageReader _images = new();
    private readonly GameRootLibraries _libraries;
    private readonly ILinuxCompatabilityDataProvider _prefix = Substitute.For<ILinuxCompatabilityDataProvider>();
    private readonly AbsolutePath _game;
    private readonly string _system32;

    public GameRootLibrariesTests()
    {
        _libraries = new GameRootLibraries(_images);
        var game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        _game = FileSystem.Shared.FromUnsanitizedFullPath(game);
        File.WriteAllBytes(Path.Combine(game, "game.exe"), [1]);

        var prefix = Directory.CreateDirectory(Path.Combine(_root, "pfx")).FullName;
        _system32 = Directory.CreateDirectory(Path.Combine(prefix, "drive_c", "windows", "system32")).FullName;
        _prefix.WinePrefixDirectoryPath.Returns(FileSystem.Shared.FromUnsanitizedFullPath(prefix));
        _prefix.GetWineDllOverrides(Arg.Any<CancellationToken>()).Returns(new ValueTask<ImmutableArray<WineDllOverride>>([]));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void WineShips(string name) => File.WriteAllBytes(Path.Combine(_system32, name), [1]);

    private void Override(string name, params WineDllOverrideType[] types)
        => _prefix.GetWineDllOverrides(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ImmutableArray<WineDllOverride>>([new WineDllOverride(name, [.. types])]));

    private ValueTask<RootLoadVerdict> HowLoaded(string name, ILinuxCompatabilityDataProvider? prefix = null)
        => _libraries.HowLoaded(_game, name, prefix ?? _prefix, CancellationToken.None);

    [Fact]
    public async Task AnImportedNameWithNoWineBuiltinIsLoaded()
    {
        _images.Imports["game.exe"] = ["openvr_api.dll"];

        (await HowLoaded("openvr_api.dll")).Should().Be(new RootLoadVerdict(RootLoad.Loaded, null));
    }

    [Fact]
    public async Task ADelayLoadedNameCountsAsImported()
    {
        _images.DelayImports["game.exe"] = ["openvr_api.dll"];

        (await HowLoaded("openvr_api.dll")).Load.Should().Be(RootLoad.Loaded);
    }

    [Fact]
    public async Task AProxyNameProtonSetsNativeIsLoaded()
    {
        WineShips("dxgi.dll");

        (await HowLoaded("dxgi.dll")).Load.Should().Be(RootLoad.Loaded);
    }

    [Fact]
    public async Task AProxyNameWineShadowsNamesTheOverride()
    {
        WineShips("version.dll");

        var verdict = await HowLoaded("version.dll");

        verdict.Load.Should().Be(RootLoad.ShadowedByWine);
        verdict.OverrideThatWouldLoad.Should().Be("version=n,b");
    }

    [Fact]
    public async Task AProxyNameWithANativeOverrideIsLoaded()
    {
        WineShips("version.dll");
        Override("version", WineDllOverrideType.Native, WineDllOverrideType.BuiltIn);

        (await HowLoaded("version.dll")).Load.Should().Be(RootLoad.Loaded);
    }

    [Fact]
    public async Task ABuiltinFirstOverrideStillShadows()
    {
        WineShips("version.dll");
        Override("version", WineDllOverrideType.BuiltIn, WineDllOverrideType.Native);

        (await HowLoaded("version.dll")).Load.Should().Be(RootLoad.ShadowedByWine);
    }

    [Fact]
    public async Task AProxyNameWithNoPrefixIsLoaded()
    {
        (await _libraries.HowLoaded(_game, "version.dll", null, CancellationToken.None)).Load.Should().Be(RootLoad.Loaded);
    }

    [Fact]
    public async Task ANameNothingImportsIsNotByName()
    {
        (await HowLoaded("tool.dll")).Should().Be(new RootLoadVerdict(RootLoad.NotByName, null));
    }

    [Fact]
    public async Task EveryExecutableInTheFolderIsRead()
    {
        File.WriteAllBytes(Path.Combine(_game.ToString(), "launcher.exe"), [1]);
        _images.Imports["game.exe"] = ["a.dll"];
        _images.Imports["launcher.exe"] = ["b.dll"];

        (await HowLoaded("a.dll")).Load.Should().Be(RootLoad.Loaded);
        (await HowLoaded("b.dll")).Load.Should().Be(RootLoad.Loaded);
    }

    // Not a proxy name, so only the import match can make it Loaded.
    [Fact]
    public async Task NamesAreCaseInsensitive()
    {
        _images.Imports["game.exe"] = ["OPENVR_API.dll"];

        (await HowLoaded("openvr_api.dll")).Load.Should().Be(RootLoad.Loaded);
        (await HowLoaded("OpenVR_Api.DLL")).Load.Should().Be(RootLoad.Loaded);
    }

    [Fact]
    public async Task WineBuiltinNamesAreCaseInsensitive()
    {
        WineShips("VERSION.DLL");

        (await HowLoaded("version.dll")).Load.Should().Be(RootLoad.ShadowedByWine);
    }

    [Fact]
    public async Task AWildcardNativeOverrideLoadsEveryName()
    {
        WineShips("version.dll");
        Override("*", WineDllOverrideType.Native, WineDllOverrideType.BuiltIn);

        (await HowLoaded("version.dll")).Load.Should().Be(RootLoad.Loaded);
    }

    [Fact]
    public async Task ANamedOverrideWinsOverTheWildcard()
    {
        WineShips("version.dll");
        _prefix.GetWineDllOverrides(Arg.Any<CancellationToken>()).Returns(new ValueTask<ImmutableArray<WineDllOverride>>(
        [
            new WineDllOverride("*", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn]),
            new WineDllOverride("version", [WineDllOverrideType.BuiltIn]),
        ]));

        (await HowLoaded("version.dll")).Load.Should().Be(RootLoad.ShadowedByWine);
    }

    [Fact]
    public async Task TheExecutablesImportsAreReadOnce()
    {
        _images.Imports["game.exe"] = ["a.dll"];

        await HowLoaded("a.dll");
        await HowLoaded("b.dll");
        _images.ImportReads.Should().Equal("game.exe");

        File.SetLastWriteTimeUtc(Path.Combine(_game.ToString(), "game.exe"), DateTime.UtcNow.AddHours(1));
        await HowLoaded("a.dll");
        _images.ImportReads.Should().Equal("game.exe", "game.exe");
    }

    [Fact]
    public async Task AnUnreadableExecutableFallsBackToTheProxyNames()
    {
        _images.Unreadable.Add("game.exe");
        _images.Imports["game.exe"] = ["openvr_api.dll"];

        (await HowLoaded("dinput8.dll")).Load.Should().Be(RootLoad.Loaded);
        (await HowLoaded("openvr_api.dll")).Load.Should().Be(RootLoad.NotByName);
    }

    [Fact]
    public async Task AFailedReadIsTriedAgainOnTheNextCall()
    {
        _images.Unreadable.Add("game.exe");
        _images.Imports["game.exe"] = ["openvr_api.dll"];
        (await HowLoaded("openvr_api.dll")).Load.Should().Be(RootLoad.NotByName);

        _images.Unreadable.Clear();

        (await HowLoaded("openvr_api.dll")).Load.Should().Be(RootLoad.Loaded);
    }
}
