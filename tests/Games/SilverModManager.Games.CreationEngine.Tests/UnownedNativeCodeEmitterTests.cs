using System.Collections.Frozen;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.App.UI.DiagnosticSystem;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Games.Generic.Diagnostics;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests;

/// <summary>
/// Native code that sits in the script extender's folders while no enabled mod provides it. The files are written to
/// the stubbed game folder and the loadout is read through the real sync tree.
/// </summary>
public class UnownedNativeCodeEmitterTests(ITestOutputHelper helper)
    : AIsolatedGameTest<UnownedNativeCodeEmitterTests, CreationEngine.SkyrimVR.SkyrimVR>(helper)
{
    private static readonly DiagnosticId UnownedNativeCodeId = new("SilverModManager.Games.Generic", number: 11);

    private static readonly GamePath Plugins = new(LocationId.Game, "Data/SKSE/Plugins");

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services).AddDiagnosticWriter()
            .AddCreationEngine()
            .AddUniversalGameLocator<CreationEngine.SkyrimVR.SkyrimVR>(new Version("1.4.15"), locationsFileSystem: IsolatedKnownFolders.Create);
    }

    // Shared by every game, so it is a service rather than part of the game's own emitter list.
    private UnownedNativeCodeEmitter Emitter => ServiceProvider.GetServices<IDiagnosticEmitter>().OfType<UnownedNativeCodeEmitter>().Single();

    private async Task<Diagnostic[]> GetAllDiagnostics(Loadout.ReadOnly loadout)
    {
        var syncTree = (await Synchronizer.BuildSyncTree(Refresh(loadout))).ToFrozenDictionary();
        return await Emitter.Diagnose(Refresh(loadout), syncTree, CancellationToken.None).ToArrayAsync();
    }

    private async Task<Diagnostic> GetSingleDiagnostic(Loadout.ReadOnly loadout)
    {
        var diagnostics = await GetAllDiagnostics(loadout);
        diagnostics.Should().ContainSingle();
        return diagnostics[0];
    }

    private async Task ShouldHaveNoDiagnostics(Loadout.ReadOnly loadout) => (await GetAllDiagnostics(loadout)).Should().BeEmpty();

    private async Task WriteOnDisk(string relativePath)
    {
        var file = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, relativePath));
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(relativePath);
    }

    private async Task<LoadoutItemGroupId> AddMod(Loadout.ReadOnly loadout, string name, string path, bool disabled = false)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout, name);
        AddFile(tx, loadout, group, new GamePath(LocationId.Game, path), content: path);
        if (disabled) tx.Add(group.Value, LoadoutItem.Disabled, Null.Instance);
        await tx.Commit();
        return group;
    }

    [Fact]
    public async Task ADllNoModProvidesIsReported()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk("Data/SKSE/Plugins/leftover.dll");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Id.Should().Be(UnownedNativeCodeId);
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        Details(diagnostic).Should().Contain("leftover.dll").And.Contain("no enabled mod");
    }

    [Fact]
    public async Task ADllAnEnabledModProvidesIsNotReported()
    {
        var loadout = await CreateLoadout();
        await AddMod(loadout, "Plugin mod", "Data/SKSE/Plugins/managed.dll");
        await WriteOnDisk("Data/SKSE/Plugins/managed.dll");

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task ADllOnlyADisabledModProvidesIsReported()
    {
        var loadout = await CreateLoadout();
        await AddMod(loadout, "Plugin mod", "Data/SKSE/Plugins/disabled.dll", disabled: true);
        await WriteOnDisk("Data/SKSE/Plugins/disabled.dll");

        var diagnostic = await GetSingleDiagnostic(loadout);

        Details(diagnostic).Should().Contain("disabled.dll");
    }

    [Fact]
    public async Task ADllAdoptedIntoOverridesIsReported()
    {
        var loadout = await CreateLoadout();
        await AddToOverrides(loadout, "Data/SKSE/Plugins/adopted.dll");
        await WriteOnDisk("Data/SKSE/Plugins/adopted.dll");

        var diagnostic = await GetSingleDiagnostic(loadout);

        Details(diagnostic).Should().Contain("adopted.dll");
    }

    [Fact]
    public async Task AnOverridesRowOffersRemoval()
    {
        var loadout = await CreateLoadout();
        await AddToOverrides(loadout, "Data/SKSE/Plugins/adopted.dll");
        await WriteOnDisk("Data/SKSE/Plugins/adopted.dll");

        var diagnostic = await GetSingleDiagnostic(loadout);

        var action = diagnostic.Actions.Should().ContainSingle(a => a.Id == DiagnosticActions.RemoveLoadoutFileId).Subject;
        var (path, group) = DiagnosticActions.ParseRemoveLoadoutFile(action.Arguments);
        path.Should().Be(new GamePath(LocationId.Game, "Data/SKSE/Plugins/adopted.dll"));
        group.Value.Should().Be(OverridesGroupLookup.Find(Refresh(loadout)));
    }

    [Fact]
    public async Task AnOverridesRowOffersSetAside()
    {
        var loadout = await CreateLoadout();
        await AddToOverrides(loadout, "Data/SKSE/Plugins/adopted.dll");
        await WriteOnDisk("Data/SKSE/Plugins/adopted.dll");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Actions.Select(a => a.Id).Should().BeEquivalentTo(
            [DiagnosticActions.RemoveLoadoutFileId, DiagnosticActions.SetAsideLoadoutFilesId, DiagnosticActions.DismissDiagnosticSubjectsId]);
        var action = diagnostic.Actions.Single(a => a.Id == DiagnosticActions.SetAsideLoadoutFilesId);
        var (paths, group) = DiagnosticActions.ParseSetAsideLoadoutFiles(action.Arguments);
        paths.Should().Equal([new GamePath(LocationId.Game, "Data/SKSE/Plugins/adopted.dll")]);
        group.Value.Should().Be(OverridesGroupLookup.Find(Refresh(loadout)));
    }

    [Fact]
    public async Task ADiskOnlyDllOffersNeitherDeleteNorSetAside()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk("Data/SKSE/Plugins/disk-only.dll");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Actions.Should().NotContain(a => a.Id == DiagnosticActions.SetAsideLoadoutFilesId);
    }

    [Fact]
    public async Task TheLoaderFilesListOffersSetAsideForTheFilesWithRows()
    {
        var loadout = await CreateLoadout();
        await AddToOverrides(loadout, "sksevr_loader.exe");
        await WriteOnDisk("sksevr_loader.exe");
        await WriteOnDisk("sksevr_1_4_15.dll");

        var diagnostic = await GetSingleDiagnostic(loadout);

        var action = diagnostic.Actions.Should().ContainSingle(a => a.Id == DiagnosticActions.SetAsideLoadoutFilesId).Subject;
        DiagnosticActions.ParseSetAsideLoadoutFiles(action.Arguments).Paths.Should().Equal([new GamePath(LocationId.Game, "sksevr_loader.exe")]);
    }

    [Fact]
    public async Task AnOverridesRowOffersIgnore()
    {
        var loadout = await CreateLoadout();
        await AddToOverrides(loadout, "Data/SKSE/Plugins/adopted.dll");
        await WriteOnDisk("Data/SKSE/Plugins/adopted.dll");

        var diagnostic = await GetSingleDiagnostic(loadout);

        var action = diagnostic.Actions.Should().ContainSingle(a => a.Id == DiagnosticActions.DismissDiagnosticSubjectsId).Subject;
        var (id, title, subjects) = DiagnosticActions.ParseDismissDiagnosticSubjects(action.Arguments);
        id.Should().Be(UnownedNativeCodeId);
        title.Should().Be(diagnostic.Title);
        subjects.Should().ContainSingle().Which.Should().Be(("Game|Data/SKSE/Plugins/adopted.dll", DiagnosticDismissals.IdentityText(await DiskIdentity(loadout, "Data/SKSE/Plugins/adopted.dll"))));
    }

    [Fact]
    public async Task ADiskOnlyDllOffersIgnoreOnly()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk("Data/SKSE/Plugins/dropped.dll");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Actions.Should().ContainSingle("there is no loadout row to remove").Which.Id.Should().Be(DiagnosticActions.DismissDiagnosticSubjectsId);
    }

    [Fact]
    public async Task AnIgnoredPathIsNotReported()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk("Data/SKSE/Plugins/ignored.dll");
        await WriteOnDisk("Data/SKSE/Plugins/other.dll");
        await Dismiss(loadout, UnownedNativeCodeId, "Game|Data/SKSE/Plugins/ignored.dll", await DiskIdentityText(loadout, "Data/SKSE/Plugins/ignored.dll"));

        var diagnostic = await GetSingleDiagnostic(loadout);

        Details(diagnostic).Should().Contain("other.dll");
    }

    [Fact]
    public async Task ANewFileAtAnIgnoredPathIsReportedAgain()
    {
        var loadout = await CreateLoadout();
        await AddToOverrides(loadout, "Data/SKSE/Plugins/swapped.dll");
        await WriteOnDisk("Data/SKSE/Plugins/swapped.dll");
        await Dismiss(loadout, UnownedNativeCodeId, "Game|Data/SKSE/Plugins/swapped.dll", await DiskIdentityText(loadout, "Data/SKSE/Plugins/swapped.dll"));
        await ShouldHaveNoDiagnostics(loadout);

        var file = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "Data/SKSE/Plugins/swapped.dll"));
        await file.WriteAllTextAsync("different bytes");

        var diagnostic = await GetSingleDiagnostic(loadout);

        Details(diagnostic).Should().Contain("swapped.dll");
    }

    [Fact]
    public async Task ADismissalWithoutAnIdentityCoversThePath()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk("Data/SKSE/Plugins/ignored.dll");
        await Dismiss(loadout, UnownedNativeCodeId, "Game|Data/SKSE/Plugins/ignored.dll", identity: null);

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task AnIgnoredLoaderFileIsDroppedFromTheListAndTheDiagnosticVanishesWhenNoneIsLeft()
    {
        var loadout = await CreateLoadout();
        await AddToOverrides(loadout, "sksevr_loader.exe");
        await AddToOverrides(loadout, "sksevr_1_4_15.dll");
        await WriteOnDisk("sksevr_loader.exe");
        await WriteOnDisk("sksevr_1_4_15.dll");
        var listId = new DiagnosticId("SilverModManager.Games.Generic", number: 12);

        await Dismiss(loadout, listId, "Game|sksevr_loader.exe", await DiskIdentityText(loadout, "sksevr_loader.exe"));
        var diagnostic = await GetSingleDiagnostic(loadout);
        Details(diagnostic).Should().Contain("sksevr_1_4_15.dll").And.NotContain("sksevr_loader.exe");
        var remove = diagnostic.Actions.Single(a => a.Id == DiagnosticActions.RemoveLoadoutFilesId);
        DiagnosticActions.ParseRemoveLoadoutFiles(remove.Arguments).Paths.Should().Equal([new GamePath(LocationId.Game, "sksevr_1_4_15.dll")]);

        await Dismiss(loadout, listId, "Game|sksevr_1_4_15.dll", await DiskIdentityText(loadout, "sksevr_1_4_15.dll"));
        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task ANewLoaderFileAtAnIgnoredPathIsListedAgain()
    {
        var loadout = await CreateLoadout();
        await AddToOverrides(loadout, "sksevr_loader.exe");
        await WriteOnDisk("sksevr_loader.exe");
        var listId = new DiagnosticId("SilverModManager.Games.Generic", number: 12);
        await Dismiss(loadout, listId, "Game|sksevr_loader.exe", await DiskIdentityText(loadout, "sksevr_loader.exe"));
        await ShouldHaveNoDiagnostics(loadout);

        var file = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "sksevr_loader.exe"));
        await file.WriteAllTextAsync("different bytes");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Id.Should().Be(listId);
        Details(diagnostic).Should().Contain("sksevr_loader.exe");
    }

    [Fact]
    public async Task TheLoaderFilesListOffersOneIgnoreForEveryListedFile()
    {
        var loadout = await CreateLoadout();
        await AddToOverrides(loadout, "sksevr_loader.exe");
        await WriteOnDisk("sksevr_loader.exe");
        await WriteOnDisk("sksevr_1_4_15.dll");

        var diagnostic = await GetSingleDiagnostic(loadout);

        var action = diagnostic.Actions.Should().ContainSingle(a => a.Id == DiagnosticActions.DismissDiagnosticSubjectsId).Subject;
        DiagnosticActions.ParseDismissDiagnosticSubjects(action.Arguments).Subjects.Select(s => s.Subject)
            .Should().BeEquivalentTo(["Game|sksevr_loader.exe", "Game|sksevr_1_4_15.dll"], "a file with no row is listed and can be ignored");
    }

    private async Task<FileIdentity> DiskIdentity(Loadout.ReadOnly loadout, string relativePath)
    {
        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        return tree[new GamePath(LocationId.Game, relativePath)].Disk.Identity;
    }

    private async Task<string> DiskIdentityText(Loadout.ReadOnly loadout, string relativePath)
        => DiagnosticDismissals.IdentityText(await DiskIdentity(loadout, relativePath));

    private async Task Dismiss(Loadout.ReadOnly loadout, DiagnosticId id, string subject, string? identity)
    {
        using var tx = Connection.BeginTransaction();
        _ = new DiagnosticDismissal.New(tx)
        {
            LoadoutId = loadout.LoadoutId,
            Source = id.Source,
            Number = id.Number,
            Subject = subject,
            Identity = identity,
            Title = "a title",
        };
        await tx.Commit();
    }

    [Fact]
    public async Task AnOverridesRowShadowingAModIsStillReported()
    {
        var loadout = await CreateLoadout();
        var mod = await AddMod(loadout, "Plugin mod", "Data/SKSE/Plugins/both.dll");
        await AddToOverrides(loadout, "Data/SKSE/Plugins/both.dll");
        await WriteOnDisk("Data/SKSE/Plugins/both.dll");

        var diagnostic = await GetSingleDiagnostic(loadout);

        var action = diagnostic.Actions.Should().ContainSingle(a => a.Id == DiagnosticActions.RemoveLoadoutFileId).Subject;
        var (_, group) = DiagnosticActions.ParseRemoveLoadoutFile(action.Arguments);
        group.Value.Should().Be(OverridesGroupLookup.Find(Refresh(loadout)));
        group.Should().NotBe(mod);
    }

    [Fact]
    public async Task EachDllGetsItsOwnDiagnostic()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk("Data/SKSE/Plugins/a.dll");
        await WriteOnDisk("Data/SKSE/Plugins/b.dll");

        var diagnostics = await GetAllDiagnostics(loadout);

        diagnostics.Should().HaveCount(2);
    }

    [Fact]
    public async Task AFileThatIsNotADllIsIgnored()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk("Data/SKSE/Plugins/settings.ini");
        await WriteOnDisk("Data/helper.exe");

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task ADllOutsideThePluginFolderAndTheGameRootIsIgnored()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk("Data/SKSE/other.dll");
        await WriteOnDisk("Data/x.dll");

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task TheScriptExtendersOwnFilesAreASuggestion()
    {
        var loadout = await CreateLoadout();
        await AddToOverrides(loadout, "sksevr_loader.exe");
        await AddToOverrides(loadout, "sksevr_1_4_15.dll");
        await WriteOnDisk("sksevr_loader.exe");
        await WriteOnDisk("sksevr_1_4_15.dll");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Id.Should().Be(new DiagnosticId("SilverModManager.Games.Generic", number: 12));
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Suggestion);
        Details(diagnostic).Should().Contain("sksevr_loader.exe").And.Contain("sksevr_1_4_15.dll");
        var action = diagnostic.Actions.Should().ContainSingle(a => a.Id == DiagnosticActions.RemoveLoadoutFilesId).Subject;
        DiagnosticActions.ParseRemoveLoadoutFiles(action.Arguments).Paths.Should().BeEquivalentTo(
            [new GamePath(LocationId.Game, "sksevr_loader.exe"), new GamePath(LocationId.Game, "sksevr_1_4_15.dll")]);
    }

    [Fact]
    public async Task AMohiddenCopyIsNotReported()
    {
        var loadout = await CreateLoadout();
        await AddToOverrides(loadout, "Data/SKSE/Plugins/x.dll.mohidden");
        await WriteOnDisk("Data/SKSE/Plugins/x.dll.mohidden");

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task AGameFileOrAnIntrinsicFileIsNotReported()
    {
        var loadout = await CreateLoadout();
        var identity = FileIdentity.XxHash3(Hash.From(1));
        var part = new SyncNodePart { Identity = identity, Size = Size.From(1), LastModifiedTicks = 0 };
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [Plugins / "vanilla.dll"] = new() { SourceItemType = LoadoutSourceItemType.Game, Disk = part, Loadout = part },
            [Plugins / "generated.dll"] = new() { SourceItemType = LoadoutSourceItemType.Intrinsic, Disk = part },
            [Plugins / "stray.dll"] = new() { SourceItemType = LoadoutSourceItemType.Loadout, Disk = part },
        }.ToFrozenDictionary();

        var diagnostics = await Emitter.Diagnose(loadout, tree, CancellationToken.None).ToArrayAsync();

        diagnostics.Should().ContainSingle();
        Summary(diagnostics[0]).Should().Contain("stray.dll");
    }

    [Fact]
    public async Task ADllAnEarlierApplyDeployedThatNoModProvidesAnymoreIsLeftToTheNextApply()
    {
        var loadout = await CreateLoadout();
        var part = new SyncNodePart { Identity = FileIdentity.XxHash3(Hash.From(1)), Size = Size.From(1), LastModifiedTicks = 0 };
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [Plugins / "removed.dll"] = new() { Disk = part, Previous = part },
        }.ToFrozenDictionary();

        (await Emitter.Diagnose(loadout, tree, CancellationToken.None).ToArrayAsync()).Should().BeEmpty();
    }

    private string Summary(Diagnostic diagnostic) => diagnostic.FormatSummary(ServiceProvider.GetRequiredService<IDiagnosticWriter>());

    private string Details(Diagnostic diagnostic) => diagnostic.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>());
}
