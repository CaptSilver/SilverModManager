using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Diagnostics.References;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.App.UI.DiagnosticSystem;
using NexusMods.Games.Generic.Diagnostics;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;
using NexusMods.StandardGameLocators.TestHelpers.StubbedGames;
using NSubstitute;
using Xunit;

namespace NexusMods.DataModel.Synchronizer.Tests;

/// <summary>
/// The diagnostics that explain a Proton game's prefix problems and the loadout items the synchronizer
/// will not deploy.
/// </summary>
public class ProtonPrefixEmitterTests(ITestOutputHelper helper) : AStubbedGameTest<ProtonPrefixEmitterTests>(helper)
{
    private const string DiagnosticSource = "NexusMods.Games.Generic";
    private const ushort MissingProtonPrefixId = 1;
    private const ushort ProtonPrefixNotLoadedId = 2;
    private const ushort HostCopyOfGameFolderId = 3;
    private const ushort LoadoutItemsTargetProtectedPathId = 4;

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services).AddDiagnosticWriter();

    private string Summary(Diagnostic diagnostic) => diagnostic.FormatSummary(ServiceProvider.GetRequiredService<IDiagnosticWriter>());
    private string Details(Diagnostic diagnostic) => diagnostic.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>());

    #region installation-level checks

    private const string PrefixPath = "/steam/steamapps/compatdata/42/pfx";
    private const string PrefixLocalAppData = PrefixPath + "/drive_c/users/steamuser/AppData/Local/Studio";

    /// <summary>
    /// A game on an in-memory host whose app data folder lives in the Proton prefix, while the same folder
    /// for a native build would sit in the host's local app data.
    /// </summary>
    private sealed class Fixture
    {
        public InMemoryFileSystem Host { get; } = new(OSInformation.FakeUnix);
        public AbsolutePath Prefix { get; }
        public AbsolutePath HostCopy { get; }
        public AbsolutePath PrefixCopy { get; }

        public Fixture()
        {
            Prefix = Host.FromUnsanitizedFullPath(PrefixPath);
            PrefixCopy = Host.FromUnsanitizedFullPath(PrefixLocalAppData);
            HostCopy = Host.GetKnownPath(KnownPath.LocalApplicationDataDirectory).Combine("Studio");
        }

        public void MakePrefixReady()
        {
            Prefix.CreateDirectory();
            using var stream = Host.CreateFile(Prefix.Combine("system.reg"));
        }

        public GameInstallation Installation(bool withProvider = false, bool nativeLinux = false, bool locationsMatchHost = false)
        {
            var game = Substitute.For<IGameData>();
            game.DisplayName.Returns("Test Game");
            var gameFolder = Host.FromUnsanitizedFullPath("/steam/steamapps/common/Test Game");

            // Asked with the host file system, a game reports the folder where its native build would keep it.
            game.GetLocations(Arg.Any<IFileSystem>(), Arg.Any<GameLocatorResult>())
                .Returns(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, gameFolder).Add(LocationId.AppData, HostCopy));

            var result = new GameLocatorResult
            {
                Game = game,
                Path = gameFolder,
                Store = GameStore.Steam,
                StoreIdentifier = "42",
                LocatorIds = ImmutableArray<LocatorId>.Empty,
                Locator = Substitute.For<IGameLocator>(),
                Platform = nativeLinux ? OSPlatform.Linux : OSPlatform.Windows,
                WinePrefixPath = nativeLinux ? null : Prefix,
                LinuxCompatabilityDataProvider = withProvider ? Substitute.For<ILinuxCompatabilityDataProvider>() : null,
            };

            var appData = locationsMatchHost || nativeLinux ? HostCopy : PrefixCopy;
            var locations = GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, gameFolder).Add(LocationId.AppData, appData));
            return new GameInstallation(result, locations);
        }
    }

    [Fact]
    public void PrefixNotReady_YieldsMissingProtonPrefixWithTheSentencePreflightUses()
    {
        var fixture = new Fixture();

        var diagnostic = ProtonPrefixEmitter.DiagnoseInstallation(fixture.Installation(), fixture.Host).Should().ContainSingle().Which;

        diagnostic.Id.Should().Be(new DiagnosticId(DiagnosticSource, MissingProtonPrefixId));
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        Summary(diagnostic).Should().Contain("Test Game");
        Details(diagnostic).Should().Contain(ProtonMessages.MissingPrefix("Test Game", fixture.Prefix));
    }

    [Fact]
    public void PrefixNotReady_DoesNotLookForHostCopies()
    {
        var fixture = new Fixture();
        fixture.HostCopy.CreateDirectory();

        var diagnostics = ProtonPrefixEmitter.DiagnoseInstallation(fixture.Installation(), fixture.Host).ToArray();

        diagnostics.Select(d => d.Id.Number).Should().Equal(MissingProtonPrefixId);
    }

    [Fact]
    public void PrefixReadyAndProviderPresent_YieldsNothing()
    {
        var fixture = new Fixture();
        fixture.MakePrefixReady();

        ProtonPrefixEmitter.DiagnoseInstallation(fixture.Installation(withProvider: true), fixture.Host).Should().BeEmpty();
    }

    [Fact]
    public void PrefixReadyButProviderMissing_YieldsProtonPrefixNotLoaded()
    {
        var fixture = new Fixture();
        fixture.MakePrefixReady();

        var diagnostic = ProtonPrefixEmitter.DiagnoseInstallation(fixture.Installation(withProvider: false), fixture.Host).Should().ContainSingle().Which;

        diagnostic.Id.Should().Be(new DiagnosticId(DiagnosticSource, ProtonPrefixNotLoadedId));
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        Details(diagnostic).Should().Contain("Restart the app");
    }

    [Fact]
    public void HostCopyExistsAndDiffers_YieldsHostCopyNamingBothPaths()
    {
        var fixture = new Fixture();
        fixture.MakePrefixReady();
        fixture.HostCopy.CreateDirectory();

        var diagnostic = ProtonPrefixEmitter.DiagnoseInstallation(fixture.Installation(withProvider: true), fixture.Host).Should().ContainSingle().Which;

        diagnostic.Id.Should().Be(new DiagnosticId(DiagnosticSource, HostCopyOfGameFolderId));
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Suggestion);
        var details = Details(diagnostic);
        details.Should().Contain(fixture.HostCopy.GetFullPath());
        details.Should().Contain(fixture.PrefixCopy.GetFullPath());
    }

    [Fact]
    public void HostCopyAbsent_YieldsNothing()
    {
        var fixture = new Fixture();
        fixture.MakePrefixReady();

        ProtonPrefixEmitter.DiagnoseInstallation(fixture.Installation(withProvider: true), fixture.Host).Should().BeEmpty();
    }

    [Fact]
    public void HostAndGamePathsEqual_YieldsNothing()
    {
        var fixture = new Fixture();
        fixture.MakePrefixReady();
        fixture.HostCopy.CreateDirectory();

        ProtonPrefixEmitter.DiagnoseInstallation(fixture.Installation(withProvider: true, locationsMatchHost: true), fixture.Host).Should().BeEmpty();
    }

    [Fact]
    public void NativeLinuxGame_YieldsNothing()
    {
        var fixture = new Fixture();
        fixture.HostCopy.CreateDirectory();

        ProtonPrefixEmitter.DiagnoseInstallation(fixture.Installation(nativeLinux: true), fixture.Host).Should().BeEmpty();
    }

    #endregion

    #region loadout-level checks

    private static readonly byte[] ModSaveBytes = "a save shipped by a mod"u8.ToArray();

    private ILoadoutDiagnosticEmitter Emitter => ServiceProvider.GetRequiredService<ProtonPrefixEmitter>();

    private async Task<Diagnostic[]> Diagnose(Loadout.ReadOnly loadout)
    {
        return await Emitter.Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, CancellationToken.None).ToArrayAsync();
    }

    private async Task<LoadoutItemGroup.ReadOnly> InstallMod(Loadout.ReadOnly loadout, Dictionary<RelativePath, byte[]> files, bool allowProtectedPaths = false)
    {
        await using var archiveFile = await CreateTestArchive(files);
        var archive = await RegisterLocalArchive(archiveFile.Path);
        var result = await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout, installer: new StubbedGameInstaller(ServiceProvider));
        var mod = result.LoadoutItemGroup!.Value;

        if (allowProtectedPaths)
        {
            using var tx = Connection.BeginTransaction();
            tx.Add(mod.Id, LoadoutItem.ProtectedPathsAllowed, Null.Instance);
            await tx.Commit();
        }

        return mod;
    }

    private static Dictionary<RelativePath, byte[]> SaveModFiles() => new()
    {
        { "saves/evil.dat", ModSaveBytes },
        { "game/ok.txt", "a normal mod file"u8.ToArray() },
    };

    [Fact]
    public async Task ItemOnSavesLocation_YieldsOneWarningNamingTheGroupAndThePath()
    {
        var loadout = await CreateLoadout();
        var mod = await InstallMod(loadout, SaveModFiles());

        var diagnostic = (await Diagnose(Refresh(loadout))).Should().ContainSingle().Which;

        diagnostic.Id.Should().Be(new DiagnosticId(DiagnosticSource, LoadoutItemsTargetProtectedPathId));
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.DataReferences.Values.OfType<LoadoutItemGroupReference>()
            .Should().ContainSingle().Which.DataId.Should().Be(mod.LoadoutItemGroupId);
        var details = Details(diagnostic);
        details.Should().Contain("evil.dat");
        details.Should().NotContain("ok.txt");
    }

    [Fact]
    public async Task ItemOnSavesLocationOfAModThatAllowsProtectedPaths_YieldsNothing()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, SaveModFiles(), allowProtectedPaths: true);

        (await Diagnose(Refresh(loadout))).Should().BeEmpty("the synchronizer deploys this mod's save files");
    }

    [Fact]
    public async Task ItemOnSavesLocationOfADisabledMod_YieldsNothing()
    {
        var loadout = await CreateLoadout();
        var mod = await InstallMod(loadout, SaveModFiles());
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(mod.Id, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }

        (await Diagnose(Refresh(loadout))).Should().BeEmpty("a disabled mod deploys nothing");
    }

    [Fact]
    public async Task ModWithoutProtectedPaths_YieldsNothing()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, new Dictionary<RelativePath, byte[]> { { "game/ok.txt", "a normal mod file"u8.ToArray() } });

        (await Diagnose(Refresh(loadout))).Should().BeEmpty();
    }

    [Fact]
    public async Task DiagnoseOnALoadoutWhosePrefixWasRemoved_YieldsMissingProtonPrefix()
    {
        var prefix = TemporaryFileManager.CreateFolder().Path.Combine("pfx");
        prefix.CreateDirectory();
        File.WriteAllText(prefix.Combine("system.reg").GetFullPath(), "WINE REGISTRY Version 2");

        var locator = ServiceProvider.GetServices<IGameLocator>().OfType<UniversalStubbedGameLocator<StubbedGame>>().Single();
        locator.Platform = OSPlatform.Windows;
        locator.WinePrefixPath = prefix;
        GameRegistry.ClearCache();
        GameInstallation = GameRegistry.LocateGameInstallations().Single(i => i.Game is StubbedGame);
        var loadout = await CreateLoadout();
        prefix.Combine("system.reg").Delete();

        var diagnostics = await Diagnose(Refresh(loadout));

        diagnostics.Select(d => d.Id.Number).Should().Equal(MissingProtonPrefixId);
    }

    #endregion
}
