using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.DiagnosticSystem;
using SilverModManager.Games.Generic.Diagnostics;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using Xunit;

namespace SilverModManager.Games.Generic.Tests;

/// <summary>
/// Native code the game loads from its own folder while no mod provides it, for a game that names no loader
/// (so only the game-root rule applies). The executables' imports come from a fake PE reader and the Wine prefix is a
/// temporary folder; the sync tree is the real one.
/// </summary>
public class UnownedNativeCodeEmitterTests(ITestOutputHelper helper)
    : ALoadoutDiagnosticEmitterTest<UnownedNativeCodeEmitterTests, NativeCodeStubGame, UnownedNativeCodeEmitter>(helper)
{
    private static readonly DiagnosticId UnownedId = new("SilverModManager.Games.Generic", number: 11);
    private static readonly DiagnosticId UnloadedId = new("SilverModManager.Games.Generic", number: 13);

    private readonly FakeNativeImageReader _images = new();
    private readonly ILinuxCompatabilityDataProvider _prefix = Substitute.For<ILinuxCompatabilityDataProvider>();
    private readonly string _scratch = Directory.CreateDirectory(TestScratch.Combine("unowned-native-code", Guid.NewGuid().ToString("N"))).FullName;
    private readonly List<GamePath> _storeFiles = [];

    private string System32 => Path.Combine(_scratch, "pfx", "drive_c", "windows", "system32");

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        Directory.CreateDirectory(System32);
        _prefix.WinePrefixDirectoryPath.Returns(NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_scratch, "pfx")));
        _prefix.GetWineDllOverrides(Arg.Any<CancellationToken>()).Returns(new ValueTask<ImmutableArray<WineDllOverride>>([]));

        return base.AddServices(services)
            .AddGenericGameSupport()
            .AddSingleton<INativeImageReader>(_images)
            .AddSingleton<StubbedFileHasherService>()
            .AddSingleton<IFileHashesService>(provider => ListingFileHashes.Over(provider.GetRequiredService<StubbedFileHasherService>(), _storeFiles))
            .AddGame<NativeCodeStubGame>()
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<NativeCodeStubGame>(
                provider,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.Steam]
            )
            {
                // Steam locator ids are numeric build ids, which the hashes queries cast; the stub hasher knows this one.
                LocatorIds = [LocatorId.From("3976631895")],
                LinuxCompatabilityDataProvider = _prefix,
            });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(_scratch, recursive: true);
    }

    // Markdown escapes the underscores in a file name.
    private string Details(Diagnostic diagnostic) => diagnostic.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>()).Replace("\\_", "_");

    private async Task WriteOnDisk(string relativePath)
    {
        var file = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, relativePath));
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(relativePath);
    }

    /// <summary>A file in the loadout's Overrides group and on disk, which is how the game folder's strays look after an apply.</summary>
    private async Task Stray(Loadout.ReadOnly loadout, string relativePath)
    {
        await AddToOverrides(loadout, relativePath);
        await WriteOnDisk(relativePath);
    }

    private void WineShips(string name) => File.WriteAllBytes(Path.Combine(System32, name), [1]);

    [Fact]
    public async Task ADllTheExecutableImportsIsAWarning()
    {
        var loadout = await CreateLoadout();
        _images.Imports["game.exe"] = ["openvr_api.dll"];
        await WriteOnDisk("game.exe");
        await Stray(loadout, "openvr_api.dll");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Id.Should().Be(UnownedId);
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        Details(diagnostic).Should().Contain("openvr_api.dll");
        diagnostic.FormatSummary(ServiceProvider.GetRequiredService<IDiagnosticWriter>()).Should().Contain("game folder");
        var remove = diagnostic.Actions.Should().ContainSingle(a => a.Id == DiagnosticActions.RemoveLoadoutFileId).Subject;
        DiagnosticActions.ParseRemoveLoadoutFile(remove.Arguments).Path.Should().Be(new GamePath(LocationId.Game, "openvr_api.dll"));
    }

    [Fact]
    public async Task ADelayLoadedDllCountsAsImported()
    {
        var loadout = await CreateLoadout();
        _images.DelayImports["game.exe"] = ["openvr_api.dll"];
        await WriteOnDisk("game.exe");
        await Stray(loadout, "openvr_api.dll");

        (await GetSingleDiagnostic(loadout)).Id.Should().Be(UnownedId);
    }

    [Fact]
    public async Task AProxyNameProtonSetsNativeIsAWarning()
    {
        var loadout = await CreateLoadout();
        WineShips("dxgi.dll");
        await Stray(loadout, "dxgi.dll");

        (await GetSingleDiagnostic(loadout)).Id.Should().Be(UnownedId);
    }

    [Fact]
    public async Task AProxyNameWineShadowsIsASuggestionNamingTheOverride()
    {
        var loadout = await CreateLoadout();
        WineShips("version.dll");
        await Stray(loadout, "version.dll");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Id.Should().Be(UnloadedId);
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Suggestion);
        Details(diagnostic).Should().Contain("version=n,b");
    }

    [Fact]
    public async Task AProxyNameWithANativeOverrideIsAWarning()
    {
        var loadout = await CreateLoadout();
        WineShips("version.dll");
        _prefix.GetWineDllOverrides(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ImmutableArray<WineDllOverride>>([new WineDllOverride("version", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn])]));
        await Stray(loadout, "version.dll");

        (await GetSingleDiagnostic(loadout)).Id.Should().Be(UnownedId);
    }

    [Fact]
    public async Task ADllTheExecutableDoesNotLoadIsASuggestion()
    {
        var loadout = await CreateLoadout();
        await Stray(loadout, "tool.dll");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Id.Should().Be(UnloadedId);
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Suggestion);
        diagnostic.Actions.Should().ContainSingle(a => a.Id == DiagnosticActions.RemoveLoadoutFileId);
    }

    [Fact]
    public async Task EveryExecutableInTheFolderIsRead()
    {
        var loadout = await CreateLoadout();
        _images.Imports["game.exe"] = ["a.dll"];
        _images.Imports["launcher.exe"] = ["b.dll"];
        await WriteOnDisk("game.exe");
        await WriteOnDisk("launcher.exe");
        await Stray(loadout, "a.dll");
        await Stray(loadout, "b.dll");

        var diagnostics = await GetAllDiagnostics(loadout);

        diagnostics.Should().HaveCount(2).And.OnlyContain(d => d.Id == UnownedId);
    }

    [Fact]
    public async Task ASecondExecutableFolderIsJudgedByItsOwnExecutables()
    {
        var loadout = await CreateLoadout();
        _images.Imports["game.exe"] = ["root-only.dll"];
        _images.Imports["bin.exe"] = ["bin-only.dll"];
        await WriteOnDisk("game.exe");
        await WriteOnDisk("bin/bin.exe");
        await Stray(loadout, "bin/root-only.dll");
        await Stray(loadout, "bin/bin-only.dll");

        var diagnostics = await GetAllDiagnostics(loadout);

        diagnostics.Should().ContainSingle(d => d.Id == UnownedId).Which.Actions
            .Single(a => a.Id == DiagnosticActions.RemoveLoadoutFileId).Arguments.Should().Contain("bin/bin-only.dll");
        diagnostics.Should().ContainSingle(d => d.Id == UnloadedId);
    }

    [Fact]
    public async Task AReplacedStoreFileOffersSteamVerification()
    {
        var loadout = await CreateLoadout();
        _images.Imports["game.exe"] = ["openvr_api.dll", "steam_api64.dll"];
        await WriteOnDisk("game.exe");
        _storeFiles.Add(new GamePath(LocationId.Game, "openvr_api.dll"));
        await Stray(loadout, "openvr_api.dll");
        await Stray(loadout, "steam_api64.dll");

        var diagnostics = await GetAllDiagnostics(loadout);

        diagnostics.Should().HaveCount(2);
        var replaced = diagnostics.Single(d => Details(d).Contains("openvr_api.dll"));
        replaced.Actions.Should().Contain(a => a.Id == DiagnosticActions.VerifyGameFilesInSteamId);
        replaced.Actions.Single(a => a.Id == DiagnosticActions.VerifyGameFilesInSteamId).Arguments.Should().Equal("3976631895");
        var other = diagnostics.Single(d => Details(d).Contains("steam_api64.dll"));
        other.Actions.Should().NotContain(a => a.Id == DiagnosticActions.VerifyGameFilesInSteamId);
    }

    [Fact]
    public async Task ADllInASubfolderOfTheGameRootIsNotLookedAt()
    {
        var loadout = await CreateLoadout();
        await Stray(loadout, "Plugins/x.dll");

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task ADiskOnlyDllOffersIgnoreOnly()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk("tool.dll");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Actions.Should().ContainSingle("there is no loadout row to remove").Which.Id.Should().Be(DiagnosticActions.DismissDiagnosticSubjectsId);
    }

    [Fact]
    public async Task AnUnloadedDllCanBeIgnoredAndStaysIgnoredUntilItsBytesChange()
    {
        var loadout = await CreateLoadout();
        await Stray(loadout, "tool.dll");
        var first = await GetSingleDiagnostic(loadout);
        var action = first.Actions.Single(a => a.Id == DiagnosticActions.DismissDiagnosticSubjectsId);
        var (id, title, subjects) = DiagnosticActions.ParseDismissDiagnosticSubjects(action.Arguments);
        id.Should().Be(UnloadedId);
        using (var tx = Connection.BeginTransaction())
        {
            foreach (var (subject, identity) in subjects)
                _ = new DiagnosticDismissal.New(tx) { LoadoutId = loadout.LoadoutId, Source = id.Source, Number = id.Number, Subject = subject, Identity = identity, Title = title };
            await tx.Commit();
        }
        await ShouldHaveNoDiagnostics(loadout.LoadoutId);

        await GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "tool.dll")).WriteAllTextAsync("different bytes");

        (await GetSingleDiagnostic(loadout)).Id.Should().Be(UnloadedId);
    }

    [Fact]
    public async Task TheExecutablesImportsAreReadOnce()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk("game.exe");
        await Stray(loadout, "a.dll");
        await Stray(loadout, "b.dll");

        await GetAllDiagnostics(loadout);
        await GetAllDiagnostics(loadout);

        _images.ImportReads.Where(name => name == "game.exe").Should().ContainSingle();
    }

    [Fact]
    public async Task AnUnreadableExecutableFallsBackToTheProxyNames()
    {
        var loadout = await CreateLoadout();
        _images.Unreadable.Add("game.exe");
        _images.Imports["game.exe"] = ["openvr_api.dll"];
        await WriteOnDisk("game.exe");
        await Stray(loadout, "dinput8.dll");
        await Stray(loadout, "openvr_api.dll");

        var diagnostics = await GetAllDiagnostics(loadout);

        diagnostics.Should().ContainSingle(d => d.Id == UnownedId).Which.Actions
            .Single(a => a.Id == DiagnosticActions.RemoveLoadoutFileId).Arguments.Should().Contain("dinput8.dll");
        diagnostics.Should().ContainSingle(d => d.Id == UnloadedId);
    }

    [Fact]
    public async Task AGameFileIsNeverReported()
    {
        var loadout = await CreateLoadout();
        _images.Imports["game.exe"] = ["steam_api64.dll"];
        await WriteOnDisk("game.exe");
        var part = new SyncNodePart { Identity = FileIdentity.XxHash3(Hash.From(1)), Size = Size.From(1), LastModifiedTicks = 0 };
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [new GamePath(LocationId.Game, "steam_api64.dll")] = new() { SourceItemType = LoadoutSourceItemType.Game, Disk = part, Loadout = part },
        }.ToFrozenDictionary();

        (await Emitter.Diagnose(loadout, tree, CancellationToken.None).ToArrayAsync()).Should().BeEmpty();
    }

    [Fact]
    public void TheEmitterRunsForAGameWithNoEmitterList()
    {
        Game.DiagnosticEmitters.Should().BeEmpty();

        ServiceProvider.GetServices<IDiagnosticEmitter>().OfType<UnownedNativeCodeEmitter>().Should().ContainSingle();
    }
}

/// <summary>A game with no Wine prefix: Windows semantics, so a name an executable imports always loads.</summary>
public class UnownedNativeCodeEmitterWithoutAPrefixTests(ITestOutputHelper helper)
    : ALoadoutDiagnosticEmitterTest<UnownedNativeCodeEmitterWithoutAPrefixTests, NativeCodeStubGame, UnownedNativeCodeEmitter>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGenericGameSupport()
            .AddSingleton<INativeImageReader>(new FakeNativeImageReader())
            .AddGame<NativeCodeStubGame>()
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<NativeCodeStubGame>(
                provider,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.Steam]
            )
            {
                LocatorIds = [LocatorId.From("3976631895")],
            });
    }

    [Fact]
    public async Task AProxyNameWithNoPrefixIsAWarning()
    {
        var loadout = await CreateLoadout();
        await AddToOverrides(loadout, "version.dll");
        var file = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "version.dll"));
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync("version.dll");

        (await GetSingleDiagnostic(loadout)).Id.Should().Be(new DiagnosticId("SilverModManager.Games.Generic", number: 11));
    }
}

/// <summary>A stubbed game with two executable folders, the game root and <c>bin</c>.</summary>
public class NativeCodeStubGame(IServiceProvider provider) : StubbedGame(provider), IGameData
{
    public IReadOnlyList<GamePath> ExecutableFolders(GameInstallation installation)
        => [new GamePath(LocationId.Game, ""), new GamePath(LocationId.Game, "bin")];
}

/// <summary>
/// Wraps the stubbed hashes service so the vanilla file list also names the paths a test says the store ships; the
/// stubbed list is a fixed archive with no DLLs.
/// </summary>
public class ListingFileHashes : DispatchProxy
{
    private IFileHashesService _inner = null!;
    private IReadOnlyList<GamePath> _extra = [];

    public static IFileHashesService Over(IFileHashesService inner, IReadOnlyList<GamePath> extra)
    {
        var proxy = Create<IFileHashesService, ListingFileHashes>();
        var self = (ListingFileHashes)(object)proxy;
        self._inner = inner;
        self._extra = extra;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        try
        {
            var result = targetMethod!.Invoke(_inner, args);
            if (targetMethod.Name == nameof(IFileHashesService.GetGameFiles) && args is [GameInstallation])
            {
                return ((IEnumerable<GameFileRecord>)result!).Concat(_extra.Select(path => new GameFileRecord
                {
                    Path = path,
                    Size = Size.From(1),
                    Identity = FileIdentity.XxHash3(Hash.From(7)),
                })).ToArray();
            }
            return result;
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            throw e.InnerException;
        }
    }
}
