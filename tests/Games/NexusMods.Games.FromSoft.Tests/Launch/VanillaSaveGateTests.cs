using System.Collections.Frozen;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FromSoft.Diagnostics;
using NexusMods.Games.FromSoft.Doctor;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Launch;
using NexusMods.Games.FromSoft.Saves;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Settings;
using NSubstitute;

namespace NexusMods.Games.FromSoft.Tests.Launch;

public class VanillaSaveGateTests(ITestOutputHelper helper) : AModdedEldenRingTest<VanillaSaveGateTests>(helper)
{
    private const ulong FirstAccount = 76561198000000001;
    private const ulong SecondAccount = 76561198000000002;

    private readonly string _prefix = Directory.CreateTempSubdirectory("silvermod-eldenring-prefix-").FullName;

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        // The factory runs after construction, when the prefix folder exists; this method runs from the base constructor.
        services.Replace(ServiceDescriptor.Singleton<IPrefixResolver>(provider => new FixedPrefixResolver(PrefixResolution.Resolved(
            new ResolvedPrefix(provider.GetRequiredService<IFileSystem>().FromUnsanitizedFullPath(_prefix), CompatibilityLayerKind.SteamProton)))));
        return services;
    }

    private VanillaSaveGate SaveGate => ServiceProvider.GetServices<ILaunchGate>().OfType<VanillaSaveGate>().Single();

    private SaveLedger Ledger => SaveLedger.For(ServiceProvider.GetRequiredService<ISettingsManager>().Get<FromSoftSettings>(), FileSystem, GameInstallation);

    private string SaveFolder(ulong account = FirstAccount)
    {
        var folder = EldenRingPaths.SaveFolder(FileSystem.FromUnsanitizedFullPath(_prefix), account).GetFullPath();
        Directory.CreateDirectory(folder);
        return folder;
    }

    private string Save(string name, string content, ulong account = FirstAccount)
    {
        var path = Path.Combine(SaveFolder(account), name);
        File.WriteAllText(path, content);
        return path;
    }

    private string[] Backups => Directory.Exists(Ledger.Backups.GetFullPath()) ? Directory.GetFiles(Ledger.Backups.GetFullPath()) : [];

    private async Task<Diagnostic[]> Diagnose(Loadout.ReadOnly loadout)
    {
        var emitter = ServiceProvider.GetServices<IFromSoftDiagnosticEmitter>().OfType<SaveWrittenByModdedSessionEmitter>().Single();
        var found = new List<Diagnostic>();
        await foreach (var diagnostic in emitter.Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, CancellationToken.None))
            found.Add(diagnostic);
        return found.ToArray();
    }

    private static void ReadOnly(string folder) => File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);

    private static void Writable(string folder) => File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    [Fact]
    public async Task AnUnchangedSaveLeavesNoCopyAndNoDiagnostic()
    {
        var loadout = await CreateModdedLoadout();
        var save = Save("ER0000.sl2", "vanilla");
        Save("ER0000.sl2.bak", "vanilla bak");

        await SaveGate.Prepare(ContextOf(loadout), CancellationToken.None);
        Directory.GetFiles(Ledger.Session.GetFullPath(), "ER0000.sl2*", SearchOption.AllDirectories).Should().HaveCount(2);
        await SaveGate.Finish(ContextOf(loadout));

        File.ReadAllText(save).Should().Be("vanilla");
        Directory.GetFiles(Ledger.Session.GetFullPath(), "*", SearchOption.AllDirectories).Should().BeEmpty();
        Ledger.Entries.Should().BeEmpty();
        Backups.Should().BeEmpty();
        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task AChangedSaveIsRestoredTheChangedBytesAreKeptAndOneCriticalDiagnosticNamesBothFiles()
    {
        var loadout = await CreateModdedLoadout();
        var save = Save("ER0000.sl2", "vanilla");
        await SaveGate.Prepare(ContextOf(loadout), CancellationToken.None);

        File.WriteAllText(save, "written by a mod");
        await SaveGate.Finish(ContextOf(loadout));

        File.ReadAllText(save).Should().Be("vanilla");
        var backup = Backups.Should().ContainSingle().Subject;
        Path.GetFileName(backup).Should().StartWith("ER0000.sl2.").And.EndWith("-modded-session");
        File.ReadAllText(backup).Should().Be("written by a mod");
        Ledger.Entries.Should().BeEmpty();

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Critical);
        var data = diagnostic.Should().BeOfType<Diagnostic<FromSoftDiagnostics.SaveWrittenByModdedSessionMessageData>>().Which.MessageData;
        data.Save.Should().Be(save);
        data.Backup.Should().Be(backup);
    }

    [Fact]
    public async Task AChangedBackupFileIsRestoredToo()
    {
        var loadout = await CreateModdedLoadout();
        Save("ER0000.sl2", "vanilla");
        var bak = Save("ER0000.sl2.bak", "vanilla bak");
        await SaveGate.Prepare(ContextOf(loadout), CancellationToken.None);

        File.WriteAllText(bak, "changed bak");
        await SaveGate.Finish(ContextOf(loadout));

        File.ReadAllText(bak).Should().Be("vanilla bak");
        File.ReadAllText(Backups.Should().ContainSingle().Subject).Should().Be("changed bak");
        Path.GetFileName(Backups.Single()).Should().StartWith("ER0000.sl2.bak.");
    }

    [Fact]
    public async Task ASaveThatDidNotExistBeforeTheSessionIsMovedAway()
    {
        var loadout = await CreateModdedLoadout();
        SaveFolder();
        await SaveGate.Prepare(ContextOf(loadout), CancellationToken.None);

        var save = Save("ER0000.sl2", "created by a mod");
        await SaveGate.Finish(ContextOf(loadout));

        File.Exists(save).Should().BeFalse();
        File.ReadAllText(Backups.Should().ContainSingle().Subject).Should().Be("created by a mod");
    }

    [Fact]
    public async Task EveryAccountFolderIsCovered()
    {
        var loadout = await CreateModdedLoadout();
        var first = Save("ER0000.sl2", "first", FirstAccount);
        var second = Save("ER0000.sl2", "second", SecondAccount);
        await SaveGate.Prepare(ContextOf(loadout), CancellationToken.None);

        File.WriteAllText(first, "changed 1");
        File.WriteAllText(second, "changed 2");
        await SaveGate.Finish(ContextOf(loadout));

        File.ReadAllText(first).Should().Be("first");
        File.ReadAllText(second).Should().Be("second");
        Backups.Select(File.ReadAllText).Should().BeEquivalentTo("changed 1", "changed 2");
        (await Diagnose(loadout)).Should().HaveCount(2);
    }

    [Fact]
    public async Task AFailedRestoreTaintsTheEntryAndKeepsTheCopy()
    {
        var loadout = await CreateModdedLoadout();
        var save = Save("ER0000.sl2", "vanilla");
        await SaveGate.Prepare(ContextOf(loadout), CancellationToken.None);
        File.WriteAllText(save, "written by a mod");

        ReadOnly(SaveFolder());
        try { await SaveGate.Finish(ContextOf(loadout)); }
        finally { Writable(SaveFolder()); }

        var entry = Ledger.Entries.Should().ContainSingle().Subject;
        entry.Tainted.Should().BeTrue();
        File.ReadAllText(Path.Combine(entry.CopyPath, "ER0000.sl2")).Should().Be("vanilla");
    }

    [Fact]
    public async Task AnUnmoddedCheckVetoesWhileTaintedAndPassesAfterResolve()
    {
        var modded = await CreateModdedLoadout();
        var save = Save("ER0000.sl2", "vanilla");
        await SaveGate.Prepare(ContextOf(modded), CancellationToken.None);
        File.WriteAllText(save, "written by a mod");
        ReadOnly(SaveFolder());
        try { await SaveGate.Finish(ContextOf(modded)); }
        finally { Writable(SaveFolder()); }

        var unmodded = await CreateLoadout();
        SaveGate.Check(ContextOf(unmodded))!.Message.Should().Contain("could not be restored").And.Contain("saves page");

        Ledger.Resolve();
        SaveGate.Check(ContextOf(unmodded)).Should().BeNull();
    }

    [Fact]
    public async Task AModdedCheckNeverVetoesOnTaint()
    {
        var loadout = await CreateModdedLoadout();
        var save = Save("ER0000.sl2", "vanilla");
        await SaveGate.Prepare(ContextOf(loadout), CancellationToken.None);
        File.WriteAllText(save, "written by a mod");
        ReadOnly(SaveFolder());
        try { await SaveGate.Finish(ContextOf(loadout)); }
        finally { Writable(SaveFolder()); }

        SaveGate.Check(ContextOf(loadout)).Should().BeNull();
    }

    [Fact]
    public async Task AnUnmoddedCheckVetoesWhenTheGameFolderHoldsAProxyDllBesideTheAntiCheatLauncher()
    {
        var loadout = await CreateLoadout();
        File.WriteAllText(Path.Combine(RealGameFolder.GetFullPath(), "dinput8.dll"), "proxy");
        File.Exists(Path.Combine(RealGameFolder.GetFullPath(), EldenRingPaths.AntiCheatLauncher)).Should().BeTrue();

        SaveGate.Check(ContextOf(loadout))!.Message.Should().Contain("dinput8.dll");
    }

    [Fact]
    public async Task AnUnmoddedCheckOfACleanInstallPasses()
    {
        var loadout = await CreateLoadout();
        Save("ER0000.sl2", "vanilla");

        SaveGate.Check(ContextOf(loadout)).Should().BeNull();
    }

    [Fact]
    public async Task ASessionWhoseFinishNeverRanIsReconciledByTheNextCheckBeforeAnythingElse()
    {
        var loadout = await CreateModdedLoadout();
        var save = Save("ER0000.sl2", "vanilla");
        await SaveGate.Prepare(ContextOf(loadout), CancellationToken.None);
        File.WriteAllText(save, "written by a mod");

        SaveGate.Check(ContextOf(loadout)).Should().BeNull();

        File.ReadAllText(save).Should().Be("vanilla");
        Backups.Should().ContainSingle();
        Ledger.Entries.Should().BeEmpty();
        (await Diagnose(loadout)).Should().ContainSingle();
    }

    [Fact]
    public async Task AnUnmoddedCheckAlsoReconcilesASessionWhoseFinishNeverRan()
    {
        var modded = await CreateModdedLoadout();
        var save = Save("ER0000.sl2", "vanilla");
        await SaveGate.Prepare(ContextOf(modded), CancellationToken.None);
        File.WriteAllText(save, "written by a mod");

        var unmodded = await CreateLoadout();
        SaveGate.Check(ContextOf(unmodded)).Should().BeNull();

        File.ReadAllText(save).Should().Be("vanilla");
    }

    [Fact]
    public async Task AReconcileThatFailsInTheNextCheckTaintsAndAnUnmoddedCheckVetoes()
    {
        var modded = await CreateModdedLoadout();
        var save = Save("ER0000.sl2", "vanilla");
        await SaveGate.Prepare(ContextOf(modded), CancellationToken.None);
        File.WriteAllText(save, "written by a mod");

        var unmodded = await CreateLoadout();
        ReadOnly(SaveFolder());
        Veto? veto;
        try { veto = SaveGate.Check(ContextOf(unmodded)); }
        finally { Writable(SaveFolder()); }

        veto!.Message.Should().Contain("could not be restored");
        Ledger.Entries.Single().Tainted.Should().BeTrue();
    }

    [Fact]
    public async Task FilesTheModuleDoesNotKnowAndTheCoopAndModdedSavesAreNeverTouched()
    {
        var loadout = await CreateModdedLoadout();
        Save("ER0000.sl2", "vanilla");
        var unknown = Save("ER0000_modded", "unknown");
        var coop = Save("ER0000.co2", "coop");
        var coopBak = Save("ER0000.co2.bak", "coop bak");
        var silver = Save("ER0000_silver.sl2", "silver");
        var silverBak = Save("ER0000_silver.sl2.bak", "silver bak");
        await SaveGate.Prepare(ContextOf(loadout), CancellationToken.None);

        Directory.GetFiles(Ledger.Session.GetFullPath(), "*", SearchOption.AllDirectories).Select(Path.GetFileName)
            .Should().BeEquivalentTo(["ER0000.sl2"]);

        foreach (var path in new[] { unknown, coop, coopBak, silver, silverBak }) File.WriteAllText(path, "changed by the session");
        await SaveGate.Finish(ContextOf(loadout));

        foreach (var path in new[] { unknown, coop, coopBak, silver, silverBak }) File.ReadAllText(path).Should().Be("changed by the session");
        Backups.Should().BeEmpty();
    }

    [Fact]
    public async Task AnotherGameIsNeitherCheckedNorPreparedNorFinished()
    {
        var loadout = await CreateModdedLoadout();
        Save("ER0000.sl2", "vanilla");
        var result = GameInstallation.LocatorResult;
        var otherGame = DispatchProxy.Create<IGameData, OtherGameProxy>();
        var other = new GameInstallation(new GameLocatorResult
        {
            Game = otherGame,
            Path = result.Path,
            Store = result.Store,
            StoreIdentifier = result.StoreIdentifier,
            LocatorIds = result.LocatorIds,
            Locator = result.Locator,
            Platform = result.Platform,
        }, GameInstallation.Locations);
        var context = ContextOf(loadout) with { Installation = other };

        SaveGate.Check(context).Should().BeNull();
        await SaveGate.Prepare(context, CancellationToken.None);
        await SaveGate.Finish(context);

        Directory.Exists(Ledger.Session.GetFullPath()).Should().BeFalse();
        File.Exists(Ledger.File.GetFullPath()).Should().BeFalse();
    }

    [Fact]
    public void AnotherGameHasNoLedgerOrBackupsFolder()
    {
        var result = GameInstallation.LocatorResult;
        var other = new GameInstallation(new GameLocatorResult
        {
            Game = DispatchProxy.Create<IGameData, OtherGameProxy>(),
            Path = result.Path,
            Store = result.Store,
            StoreIdentifier = result.StoreIdentifier,
            LocatorIds = result.LocatorIds,
            Locator = result.Locator,
            Platform = result.Platform,
        }, GameInstallation.Locations);
        var settings = ServiceProvider.GetRequiredService<ISettingsManager>();

        FluentActions.Invoking(() => SaveLedger.For(settings.Get<FromSoftSettings>(), FileSystem, other)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => SaveBackups.For(settings, FileSystem, other)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task TheLedgerIsAJsonFileWithTheDocumentedFields()
    {
        var loadout = await CreateModdedLoadout();
        Save("ER0000.sl2", "vanilla");
        await SaveGate.Prepare(ContextOf(loadout), CancellationToken.None);

        var json = await File.ReadAllTextAsync(Ledger.File.GetFullPath());
        json.Should().Contain("\"folder\"").And.Contain("\"hash\"").And.Contain("\"copyPath\"").And.Contain("\"tainted\"").And.Contain("\"stamp\"");
        Ledger.File.GetFullPath().Should().EndWith($"EldenRing/{EldenRingPaths.InstallationFolderName(GameInstallation.LocatorResult)}/saves/ledger.json");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(_prefix, recursive: true);
    }

    /// <summary>A game that answers only its id; NSubstitute cannot make a default <see cref="GameId"/> to configure.</summary>
    public class OtherGameProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == "get_GameId" ? GameId.From("Other.Game") : null;
    }

    private sealed class FixedPrefixResolver(PrefixResolution resolution) : IPrefixResolver
    {
        public PrefixResolution Resolve(GameInstallation installation) => resolution;
    }
}
