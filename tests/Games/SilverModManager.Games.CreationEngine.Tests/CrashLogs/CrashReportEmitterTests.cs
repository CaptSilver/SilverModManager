using System.Collections.Frozen;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.DiagnosticSystem;
using SilverModManager.Games.CreationEngine.CrashLogs;
using SilverModManager.Games.CreationEngine.Emitters;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.CrashLogs;

/// <summary>
/// Crash logs written into the prefix's script extender folder, read through the real sync tree. The loadout's mods
/// are rows without bytes in the store, so these tests never apply the loadout: the emitter is read through
/// <c>BuildSyncTree</c>, which is what the diagnostic manager uses and never writes.
/// </summary>
public class CrashReportEmitterTests(ITestOutputHelper helper)
    : AIsolatedGameTest<CrashReportEmitterTests, CreationEngine.SkyrimVR.SkyrimVR>(helper)
{
    private const string Source = "SilverModManager.Games.CreationEngine";
    private static readonly DiagnosticId NamingAMod = new(Source, number: 10);
    private static readonly DiagnosticId Repeated = new(Source, number: 11);
    private static readonly DiagnosticId OneOff = new(Source, number: 12);
    private static readonly DiagnosticId Startup = new(Source, number: 13);

    private const string Mesh = "Data/meshes/Plants/GlowingMushroomCluster01.nif";
    private const string SynthesisPatch = "Data/Yggdrasil - Synthesis Patch.esp";
    private const string NoLooseFile = "no loose file in this loadout supplies it";

    private readonly CapturedLogs _logs = new();
    private CrashReportEmitter? _emitter;

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return _logs.AddTo(base.AddServices(services)
            .AddDiagnosticWriter()
            .AddCreationEngine()
            .AddUniversalGameLocator<CreationEngine.SkyrimVR.SkyrimVR>(new Version("1.4.15"), locationsFileSystem: IsolatedKnownFolders.Create));
    }

    // One per test, so a test counting parses starts from an empty cache.
    private CrashReportEmitter Emitter => _emitter ??= new CrashReportEmitter(Game, ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<CrashReportEmitter>());

    private AbsolutePath LogFolder => GameInstallation.Locations.ToAbsolutePath(Game.ScriptExtenderLogFolder!.Value);

    private async Task<AbsolutePath> WriteLog(string fixture, string? name = null, DateTime? lastWriteUtc = null)
    {
        LogFolder.CreateDirectory();
        var file = LogFolder / (name ?? $"crash-{fixture}");
        await file.WriteAllBytesAsync(await File.ReadAllBytesAsync(Fixtures.Path(fixture)));
        File.SetLastWriteTimeUtc(file.ToString(), lastWriteUtc ?? DateTime.UtcNow);
        return file;
    }

    private async Task<LoadoutItemGroupId> AddMod(Loadout.ReadOnly loadout, string name, params string[] gamePaths)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout, name);
        foreach (var path in gamePaths) AddFile(tx, loadout, group, new GamePath(LocationId.Game, path));
        var committed = await tx.Commit();
        return LoadoutItemGroupId.From(committed[group.Value]);
    }

    // The files a log says the game loaded, as one mod, so a log is "from this loadout" whatever else a test adds.
    private async Task AddLoadedFiles(Loadout.ReadOnly loadout, string fixture, params string[] except)
    {
        var report = Fixtures.Parse(fixture);
        var paths = report.Plugins.Select(p => $"Data/{p}")
            .Concat(report.SksePlugins.Select(p => $"Data/SKSE/Plugins/{p.Name}"))
            .Where(p => !except.Contains(p, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        await AddMod(loadout, "Loaded by the game", paths);
    }

    private async Task<Diagnostic[]> Diagnose(Loadout.ReadOnly loadout)
    {
        loadout = Refresh(loadout);
        var tree = (await Synchronizer.BuildSyncTree(loadout)).ToFrozenDictionary();
        return await Emitter.Diagnose(loadout, tree, CancellationToken.None).ToArrayAsync();
    }

    private string Details(Diagnostic diagnostic) => diagnostic.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>());

    private string Summary(Diagnostic diagnostic) => diagnostic.FormatSummary(ServiceProvider.GetRequiredService<IDiagnosticWriter>());

    // The owner lines are bullets: "- Name: `path` (...)".
    private IEnumerable<string> Lines(Diagnostic diagnostic) => Details(diagnostic).Split('\n').Select(l => l.TrimStart('-', ' '));

    private async Task<Loadout.ReadOnly> TwoEnbLogsFor(Loadout.ReadOnly loadout, params string[] except)
    {
        await AddLoadedFiles(loadout, "enb-light-mushroom-a.log", except);
        await WriteLog("enb-light-mushroom-a.log");
        await WriteLog("enb-light-mushroom-b.log");
        return loadout;
    }

    [Fact]
    public async Task NoLogsMeansNoDiagnostics()
    {
        var loadout = await CreateLoadout();

        (await Diagnose(loadout)).Should().BeEmpty();
        LogFolder.DirectoryExists().Should().BeFalse("the emitter reads the folder, it never creates it");
    }

    [Fact]
    public async Task ALogOlderThanTheWindowIsIgnored()
    {
        var loadout = await CreateLoadout();
        await WriteLog("enb-light-mushroom-a.log", lastWriteUtc: DateTime.UtcNow.AddDays(-15));

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task TwoEnbLogsBecomeOneWarningNamingTheMeshOwner()
    {
        var loadout = await CreateLoadout();
        var mod = await AddMod(loadout, "Glowing Mushrooms", Mesh);
        await TwoEnbLogsFor(loadout);

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        diagnostic.Id.Should().Be(NamingAMod);
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        Summary(diagnostic).Should().Contain("2 crashes");
        Details(diagnostic).Should().Contain("Glowing Mushrooms").And.Contain("ENB");
        diagnostic.DataReferences.Values.OfType<LoadoutItemGroupReference>().Should().ContainSingle()
            .Which.DataId.Should().Be(mod);
    }

    [Fact]
    public async Task AGroupWhoseNamedModIsNowDisabledIsDropped()
    {
        var loadout = await CreateLoadout();
        var mod = await AddMod(loadout, "Glowing Mushrooms", Mesh);
        await TwoEnbLogsFor(loadout);
        (await Diagnose(loadout)).Should().ContainSingle();

        await DisableItem(mod.Value);

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task OwnersOtherThanTheNamedModArePlainNames()
    {
        var loadout = await CreateLoadout();
        await AddMod(loadout, "Glowing Mushrooms", Mesh);
        await AddMod(loadout, "Synthesis Patch", SynthesisPatch);
        await TwoEnbLogsFor(loadout, SynthesisPatch);

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        Details(diagnostic).Should().Contain("Synthesis Patch");
        diagnostic.DataReferences.Values.OfType<LoadoutItemGroupReference>().Should().ContainSingle();
    }

    [Fact]
    public async Task AFileInOverridesIsNotNamedAsAMod()
    {
        var loadout = await CreateLoadout();
        await AddToOverrides(loadout, Mesh);
        await TwoEnbLogsFor(loadout);

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle("an Overrides row is not disabled, so the group stays").Subject;

        diagnostic.Id.Should().Be(Repeated);
        Details(diagnostic).Should().NotContain("Overrides").And.Contain(NoLooseFile);
    }

    [Fact]
    public async Task AnObjectNoLooseFileSuppliesSaysSoAndLeadsWithItsPluginOwner()
    {
        var loadout = await CreateLoadout();
        await AddMod(loadout, "Synthesis Patch", SynthesisPatch);
        await AddLoadedFiles(loadout, "enb-light-mushroom-a.log", SynthesisPatch);
        await WriteLog("enb-light-mushroom-a.log");

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        Lines(diagnostic).Should().Contain(l => l.StartsWith("Synthesis Patch: ") && l.Contains("GlowingMushroomCluster01.nif") && l.Contains(NoLooseFile));
    }

    [Fact]
    public async Task ASingleEngineCrashIsASuggestionLabelledSeenOnce()
    {
        var loadout = await CreateLoadout();
        await AddLoadedFiles(loadout, "light-list-bread.log");
        await WriteLog("light-list-bread.log");

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        diagnostic.Id.Should().Be(OneOff);
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Suggestion);
        Details(diagnostic).Should().Contain("Seen once");
    }

    [Fact]
    public async Task TheDllAtFrameOneOfTheRendererCrashIsListedAsPassedThrough()
    {
        const string dll = "Data/SKSE/Plugins/OpenAnimationReplacer.dll";
        var loadout = await CreateLoadout();
        await AddMod(loadout, "OAR", dll);
        await AddLoadedFiles(loadout, "renderer-init.log", dll);
        await WriteLog("renderer-init.log");

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        diagnostic.Id.Should().Be(Startup);
        Details(diagnostic).Should().Contain("OpenAnimationReplacer.dll").And.Contain("passed through").And.NotContain("caused");
    }

    [Fact]
    public async Task EngineFixesAtFrameSevenIsNotListed()
    {
        const string dll = "Data/SKSE/Plugins/EngineFixes.dll";
        var loadout = await CreateLoadout();
        await AddMod(loadout, "Engine Fixes", dll);
        await AddLoadedFiles(loadout, "enb-light-mushroom-a.log", dll);
        await WriteLog("enb-light-mushroom-a.log");

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        Details(diagnostic).Should().NotContain("EngineFixes.dll").And.NotContain("d3d11.dll");
    }

    [Fact]
    public async Task AnUnreadableLogIsNamedInMoreDetailsAndLoggedAtDebug()
    {
        const string unreadable = "crash-2026-11-09-20-00-00.log";
        var loadout = await CreateLoadout();
        await AddLoadedFiles(loadout, "light-list-bread.log");
        await WriteLog("unknown-format.log", name: unreadable);
        await WriteLog("light-list-bread.log");

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        diagnostic.MoreDetails.Should().Contain(unreadable);
        _logs.Lines.Should().Contain(l => l.Level == LogLevel.Debug && l.Message.Contains(unreadable));
        _logs.Lines.Should().NotContain(l => l.Level >= LogLevel.Warning && l.Message.Contains(unreadable));
    }

    [Fact]
    public async Task ParsedLogsAreNotReparsedOnASecondRun()
    {
        var loadout = await CreateLoadout();
        await TwoEnbLogsFor(loadout);

        await Diagnose(loadout);
        await Diagnose(loadout);

        _logs.Lines.Count(l => l.Message.StartsWith("Parsed crash log")).Should().Be(2);
    }

    [Fact]
    public async Task ARendererInitCrashIsCriticalUntilTheGameIsLaunchedAgain()
    {
        var loadout = await CreateLoadout();
        await AddLoadedFiles(loadout, "renderer-init.log");
        await WriteLog("renderer-init.log", lastWriteUtc: DateTime.UtcNow.AddHours(-1));

        var before = (await Diagnose(loadout)).Should().ContainSingle().Subject;
        before.Id.Should().Be(Startup);
        before.Severity.Should().Be(DiagnosticSeverity.Critical);
        Details(before).Should().NotContain("launched since");

        var launchLog = LogFolder / "sksevr.log";
        await launchLog.WriteAllTextAsync("loader started");
        File.SetLastWriteTimeUtc(launchLog.ToString(), DateTime.UtcNow);

        var after = (await Diagnose(loadout)).Should().ContainSingle().Subject;
        after.Id.Should().Be(Startup);
        after.Severity.Should().Be(DiagnosticSeverity.Warning);
        Details(after).Should().Contain("launched since");
    }

    [Fact]
    public async Task AMissingMastersLogIsCritical()
    {
        var loadout = await CreateLoadout();
        await AddLoadedFiles(loadout, "missing-masters.log");
        await WriteLog("missing-masters.log");

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        diagnostic.Id.Should().Be(Startup);
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Critical);
        Details(diagnostic).Should().Contain("Missing Master");
    }

    [Fact]
    public async Task ALogWhosePluginsAreNotThisLoadoutsIsLabelledAndNotBlamed()
    {
        var loadout = await CreateLoadout();
        await AddMod(loadout, "Glowing Mushrooms", Mesh);
        await WriteLog("enb-light-mushroom-a.log");
        await WriteLog("enb-light-mushroom-b.log");

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        diagnostic.Id.Should().Be(Repeated);
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Suggestion);
        Details(diagnostic).Should().Contain("another loadout").And.Contain("12 of the 12");
        diagnostic.DataReferences.Values.OfType<LoadoutItemGroupReference>().Should().BeEmpty();
    }

    [Fact]
    public async Task RemovingOneModAfterTheCrashDoesNotRelabelTheGroup()
    {
        const string waterFixes = "Data/Landscape and Water Fixes.esp";
        var loadout = await CreateLoadout();
        await AddMod(loadout, "Glowing Mushrooms", Mesh);
        var water = await AddMod(loadout, "Water Fixes", waterFixes);
        await TwoEnbLogsFor(loadout, waterFixes);

        var before = (await Diagnose(loadout)).Should().ContainSingle().Subject;
        before.Id.Should().Be(NamingAMod);
        Details(before).Should().Contain("this loadout");

        using (var tx = Connection.BeginTransaction())
        {
            tx.Delete(water.Value, recursive: true);
            await tx.Commit();
        }

        var after = (await Diagnose(loadout)).Should().ContainSingle().Subject;
        after.Id.Should().Be(NamingAMod);
        Details(after).Should().Contain("this loadout").And.NotContain("another loadout");
    }

    [Fact]
    public async Task TheFileIndexListsEveryRowAndMarksTheDisabledOnes()
    {
        var loadout = await CreateLoadout();
        await AddMod(loadout, "Plugin mod", "Data/Some Mod.esp", "Data/SKSE/Plugins/some.dll", "Data/meshes/x.nif");
        var off = await AddMod(loadout, "Off mod", "Data/Off.esp", "Data/SKSE/Plugins/off.dll", "Data/meshes/y.nif");
        await DisableItem(off.Value);

        var index = LoadoutFileIndex.For(Game, Refresh(loadout));

        index.Paths.Should().HaveCount(6);
        index.DisabledPaths.Should().BeEquivalentTo([
            new GamePath(LocationId.Game, "Data/Off.esp"),
            new GamePath(LocationId.Game, "Data/SKSE/Plugins/off.dll"),
            new GamePath(LocationId.Game, "Data/meshes/y.nif"),
        ]);
        index.PluginNames.Should().BeEquivalentTo(["Some Mod.esp", "Off.esp"]);
        index.SkseDllNames.Should().BeEquivalentTo(["some.dll", "off.dll"]);
    }
}
