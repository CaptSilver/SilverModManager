using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Generic.Diagnostics;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using Xunit;

namespace SilverModManager.Games.Generic.Tests;

/// <summary>
/// The warning for OpenComposite without a registered OpenXR runtime. The config folder is a temporary one the
/// test owns, so no test reads the real home.
/// </summary>
public class OpenXrRuntimeEmitterTests(ITestOutputHelper helper)
    : ALoadoutDiagnosticEmitterTest<OpenXrRuntimeEmitterTests, VrStubbedGame, OpenXrRuntimeEmitter>(helper)
{
    private static readonly DiagnosticId NotRegisteredId = new("SilverModManager.Games.Generic", number: 10);

    private readonly FakeOpenXrConfig _config = new(Directory.CreateDirectory(TestScratch.Combine("openxr", Guid.NewGuid().ToString("N"))).FullName);

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGenericGameSupport()
            .AddSingleton<IOpenXrConfig>(_config)
            .AddGame<VrStubbedGame>()
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<VrStubbedGame>(
                provider,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.Steam]
            )
            {
                LocatorIds = [LocatorId.From("3976631895")],
            });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(_config.Root, recursive: true);
    }

    private string Details(Diagnostic diagnostic) => diagnostic.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>());

    private async Task DeployMod(Loadout.ReadOnly loadout, params string[] paths)
    {
        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout, "OpenComposite");
            foreach (var path in paths) AddFile(tx, loadout, group, new GamePath(LocationId.Game, path));
            await tx.Commit();
        }

        foreach (var path in paths)
            await GameFolderFiles.Write(GameInstallation, path);
    }

    private Task<Loadout.ReadOnly> WithOpenComposite() => CreateLoadout().ContinueWith(async t =>
    {
        await DeployMod(t.Result, "openvr_api.dll", "opencomposite.ini");
        return t.Result;
    }).Unwrap();

    private async Task<AbsolutePath> WriteRuntime(string manifestJson, string? libraryFile = null)
    {
        var manifest = _config.ActiveRuntimeFile;
        manifest.Parent.CreateDirectory();
        await manifest.WriteAllTextAsync(manifestJson);
        if (libraryFile is not null) await (manifest.Parent / libraryFile).WriteAllTextAsync("so");
        return manifest;
    }

    [Fact]
    public async Task ANoRegisteredRuntimeIsReported()
    {
        var loadout = await WithOpenComposite();

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Id.Should().Be(NotRegisteredId);
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        var details = Details(diagnostic);
        details.Should().Contain("does not exist").And.Contain("Set SteamVR as OpenXR Runtime").And.Contain("steamxr_linux64.json");
        details.Should().Contain(_config.ActiveRuntimeFile.GetFullPath());
    }

    [Fact]
    public async Task AManifestThatNamesAMissingLibraryIsReported()
    {
        var loadout = await WithOpenComposite();
        await WriteRuntime("""{"file_format_version":"1.0.0","runtime":{"library_path":"./bin/vrclient.so"}}""");

        var diagnostic = await GetSingleDiagnostic(loadout);

        Details(diagnostic).Should().Contain("./bin/vrclient.so").And.Contain("does not exist");
    }

    [Fact]
    public async Task AMissingLibraryIsNamedInPlainTextForTheWriterToEscape()
    {
        var loadout = await WithOpenComposite();
        await WriteRuntime("""{"file_format_version":"1.0.0","runtime":{"library_path":"./lib/libopenxr_monado.so"}}""");

        var diagnostic = await GetSingleDiagnostic(loadout);

        // Backticks written here would be swapped for lookalikes, because the writer escapes a plain string.
        diagnostic.FormatDetails(new PlainDiagnosticWriter()).Should().Contain("and it points to ./lib/libopenxr_monado.so, which does not exist.");
    }

    [Fact]
    public async Task AManifestThatIsNotJsonIsReported()
    {
        var loadout = await WithOpenComposite();
        await WriteRuntime("not json");

        var diagnostic = await GetSingleDiagnostic(loadout);

        Details(diagnostic).Should().Contain("not a readable runtime manifest");
    }

    [Fact]
    public async Task AManifestWithItsLibraryIsFine()
    {
        var loadout = await WithOpenComposite();
        await WriteRuntime("""{"file_format_version":"1.0.0","runtime":{"library_path":"./vrclient.so"}}""", libraryFile: "vrclient.so");

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task ALinkToAManifestInAnotherFolderResolvesTheLibraryFromTheTarget()
    {
        var loadout = await WithOpenComposite();
        var steamvr = Directory.CreateDirectory(Path.Combine(_config.Root, "steamvr"));
        File.WriteAllText(Path.Combine(steamvr.FullName, "steamxr_linux64.json"), """{"runtime":{"library_path":"bin/vrclient.so"}}""");
        Directory.CreateDirectory(Path.Combine(steamvr.FullName, "bin"));
        File.WriteAllText(Path.Combine(steamvr.FullName, "bin", "vrclient.so"), "so");
        _config.ActiveRuntimeFile.Parent.CreateDirectory();
        File.CreateSymbolicLink(_config.ActiveRuntimeFile.GetFullPath(), Path.Combine(steamvr.FullName, "steamxr_linux64.json"));

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task ADanglingLinkCountsAsMissing()
    {
        var loadout = await WithOpenComposite();
        _config.ActiveRuntimeFile.Parent.CreateDirectory();
        File.CreateSymbolicLink(_config.ActiveRuntimeFile.GetFullPath(), Path.Combine(_config.Root, "gone.json"));

        var diagnostic = await GetSingleDiagnostic(loadout);

        Details(diagnostic).Should().Contain("does not exist");
    }

    [Fact]
    public async Task ABareLibraryNameIsLeftToTheSystemSearch()
    {
        var loadout = await WithOpenComposite();
        await WriteRuntime("""{"runtime":{"library_path":"libopenxr_monado.so"}}""");

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task WithoutTheIniTheLibraryIsNotTakenForOpenComposite()
    {
        var loadout = await CreateLoadout();
        await DeployMod(loadout, "openvr_api.dll");

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task AnIniWithoutALibraryIsNotOpenComposite()
    {
        var loadout = await CreateLoadout();
        await DeployMod(loadout, "opencomposite.ini");

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task ALibraryNoModProvidesIsNotOpenComposite()
    {
        var loadout = await CreateLoadout();
        await GameFolderFiles.Write(GameInstallation, "openvr_api.dll");
        await GameFolderFiles.Write(GameInstallation, "opencomposite.ini");

        await ShouldHaveNoDiagnostics(loadout);
    }
}

/// <summary>A game that is not a VR title: OpenComposite files in its loadout mean nothing.</summary>
public class OpenXrRuntimeEmitterOnAFlatGameTests(ITestOutputHelper helper)
    : ALoadoutDiagnosticEmitterTest<OpenXrRuntimeEmitterOnAFlatGameTests, StubbedGame, OpenXrRuntimeEmitter>(helper)
{
    private readonly FakeOpenXrConfig _config = new(Directory.CreateDirectory(TestScratch.Combine("openxr", Guid.NewGuid().ToString("N"))).FullName);

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGenericGameSupport()
            .AddSingleton<IOpenXrConfig>(_config)
            .AddGame<StubbedGame>()
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<StubbedGame>(
                provider,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.Steam]
            )
            {
                LocatorIds = [LocatorId.From("3976631895")],
            });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(_config.Root, recursive: true);
    }

    [Fact]
    public async Task OpenCompositeInANonVrGameIsNotReported()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout, "OpenComposite");
            AddFile(tx, loadout, group, new GamePath(LocationId.Game, "openvr_api.dll"));
            AddFile(tx, loadout, group, new GamePath(LocationId.Game, "opencomposite.ini"));
            await tx.Commit();
        }
        foreach (var path in new[] { "openvr_api.dll", "opencomposite.ini" })
            await GameFolderFiles.Write(GameInstallation, path);

        await ShouldHaveNoDiagnostics(loadout);
    }
}

/// <summary>Writes a file into the stubbed game folder, which the stub leaves uncreated.</summary>
internal static class GameFolderFiles
{
    public static async Task Write(GameInstallation installation, string path)
    {
        var file = installation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, path));
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(path);
    }
}

/// <summary>An OpenXR config whose active runtime file lives in a folder the test owns.</summary>
internal sealed class FakeOpenXrConfig(string root) : IOpenXrConfig
{
    public string Root { get; } = root;

    public AbsolutePath ActiveRuntimeFile => FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(Root, "config", "openxr", "1", "active_runtime.json"));
}

/// <summary>A stubbed game that is a VR title.</summary>
public class VrStubbedGame(IServiceProvider provider) : StubbedGame(provider), IVirtualRealityGame;
