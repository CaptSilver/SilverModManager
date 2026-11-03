using System.Collections.Frozen;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.App.UI.DiagnosticSystem;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Games.CreationEngine.Emitters;
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
/// Plugin DLLs that sit in the script extender's folder while no enabled mod provides them. The files are written to
/// the stubbed game folder and the loadout is read through the real sync tree.
/// </summary>
public class UnmanagedScriptExtenderPluginEmitterTests(ITestOutputHelper helper)
    : AIsolatedGameTest<UnmanagedScriptExtenderPluginEmitterTests, CreationEngine.SkyrimVR.SkyrimVR>(helper)
{
    private static readonly DiagnosticId UnmanagedPluginId = new("SilverModManager.Games.CreationEngine", number: 9);

    private static readonly GamePath Plugins = new(LocationId.Game, "Data/SKSE/Plugins");

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services).AddDiagnosticWriter()
            .AddCreationEngine()
            .AddUniversalGameLocator<CreationEngine.SkyrimVR.SkyrimVR>(new Version("1.4.15"), locationsFileSystem: IsolatedKnownFolders.Create);
    }

    // The emitter belongs to the game and is not a service, so it is found through the game.
    private UnmanagedScriptExtenderPluginEmitter Emitter => Game.DiagnosticEmitters.OfType<UnmanagedScriptExtenderPluginEmitter>().Single();

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

        diagnostic.Id.Should().Be(UnmanagedPluginId);
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
    public async Task ADiskOnlyDllOffersNoRemoval()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk("Data/SKSE/Plugins/dropped.dll");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Actions.Should().BeEmpty("there is no loadout row to remove");
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
    public async Task FilesThatAreNotDllsOrNotInThePluginFolderAreIgnored()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk("Data/SKSE/Plugins/settings.ini");
        await WriteOnDisk("Data/SKSE/other.dll");
        await WriteOnDisk("helper.dll");

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
