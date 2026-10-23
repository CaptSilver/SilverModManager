using System.Reactive.Linq;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Games;
using NexusMods.App.UI.Pages.FromSoftSaves;
using NexusMods.App.UI.Windows;
using NexusMods.Games.FromSoft;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Saves;
using FromSoftSavesService = NexusMods.Games.FromSoft.Saves.FromSoftSaves;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Settings;
using NSubstitute;
using ReactiveUI;

namespace NexusMods.UI.Tests;

public class FromSoftSavesViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    private const string Account = "76561198000000001";

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("silvermod-saves-vm-").FullName;
        public IProcessTracker Tracker { get; } = Substitute.For<IProcessTracker>();
        public FromSoftSavesViewModel Vm { get; }
        public string SaveFolder => Path.Combine(Root, "prefix", "drive_c/users/steamuser/AppData/Roaming/EldenRing", Account);
        public string DataRoot => Path.Combine(Root, "data");

        public Fixture(IServiceProvider provider, bool steamRunning)
        {
            Tracker.IsSteamRunning().Returns(steamRunning);
            Directory.CreateDirectory(SaveFolder);

            var fileSystem = FileSystem.Shared;
            var prefixes = Substitute.For<IPrefixResolver>();
            prefixes.Resolve(Arg.Any<GameInstallation>()).Returns(PrefixResolution.Resolved(
                new ResolvedPrefix(fileSystem.FromUnsanitizedFullPath(Path.Combine(Root, "prefix")), CompatibilityLayerKind.SteamProton)));
            var settings = Substitute.For<ISettingsManager>();
            settings.Get<FromSoftSettings>().Returns(new FromSoftSettings { DataRoot = new ConfigurablePath(KnownPath.EntryDirectory, Path.GetRelativePath(AppContext.BaseDirectory, DataRoot)) });

            var saves = new FromSoftSavesService(settings, fileSystem, prefixes, Tracker, TimeProvider.System, NullLogger<FromSoftSavesService>.Instance);
            Vm = new FromSoftSavesViewModel(Substitute.For<IWindowManager>(), provider.GetRequiredService<NexusMods.MnemonicDB.Abstractions.IConnection>(), saves, NullLogger<FromSoftSavesViewModel>.Instance)
            {
                Installation = EldenRingInstallation(provider),
            };
        }

        // The UI host locates no FromSoft game, so the located installation is rebuilt around Elden Ring's game class.
        private static GameInstallation EldenRingInstallation(IServiceProvider provider)
        {
            var located = provider.GetRequiredService<IGameRegistry>().LocateGameInstallations().First();
            var result = located.LocatorResult;
            return new GameInstallation(new GameLocatorResult
            {
                Game = provider.GetRequiredService<EldenRingGame>(),
                Path = result.Path,
                Store = result.Store,
                StoreIdentifier = result.StoreIdentifier,
                LocatorIds = result.LocatorIds,
                Locator = result.Locator,
                Platform = result.Platform,
                LinuxCompatabilityDataProvider = result.LinuxCompatabilityDataProvider,
                WinePrefixPath = result.WinePrefixPath,
                Compatibility = result.Compatibility,
                StoreRoot = result.StoreRoot,
                StoreBuildId = result.StoreBuildId,
                StoreUpdatePending = result.StoreUpdatePending,
                StoreLastWrite = result.StoreLastWrite,
            }, located.Locations);
        }

        public string Write(string name, string content)
        {
            var path = Path.Combine(SaveFolder, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    [Fact]
    public void TheFolderListingShowsKnownSavesAndUnknownFiles()
    {
        using var fixture = new Fixture(Provider, steamRunning: false);
        fixture.Write("ER0000.sl2", "vanilla");
        fixture.Write("ER0000.co2", "coop");
        fixture.Write("ER0000_modded", "someone else's");
        fixture.Vm.RefreshCommand.Execute().Subscribe();

        fixture.Vm.Folders.Should().ContainSingle().Which.SteamId.Should().Be(Account);
        fixture.Vm.SelectedFolder.Should().NotBeNull();
        fixture.Vm.Files.Select(f => (f.Name, f.IsKnown)).Should().Equal(("ER0000.co2", true), ("ER0000.sl2", true), ("ER0000_modded", false));
        fixture.Vm.Problem.Should().BeEmpty();
    }

    [Fact]
    public void TheSelectedFolderAndFileSurviveARefreshThatReplacesTheRows()
    {
        using var fixture = new Fixture(Provider, steamRunning: false);
        fixture.Write("ER0000.sl2", "vanilla");
        var otherFolder = Path.Combine(Path.GetDirectoryName(fixture.SaveFolder)!, "76561198000000002");
        Directory.CreateDirectory(otherFolder);
        File.WriteAllText(Path.Combine(otherFolder, "ER0000.sl2"), "other account");
        File.WriteAllText(Path.Combine(otherFolder, "ER0000.co2"), "other account coop");
        // The list views clear their selection whenever their rows are replaced, before the refresh restores it.
        fixture.Vm.WhenAnyValue(vm => vm.Folders).Subscribe(_ => fixture.Vm.SelectedFolder = null);
        fixture.Vm.WhenAnyValue(vm => vm.Files).Subscribe(_ => fixture.Vm.SelectedFile = null);
        fixture.Vm.RefreshCommand.Execute().Subscribe();
        fixture.Vm.SelectedFolder = fixture.Vm.Folders.Single(f => f.SteamId == "76561198000000002");
        fixture.Vm.SelectedFile = fixture.Vm.Files.Single(f => f.Name == "ER0000.co2");

        fixture.Vm.RefreshCommand.Execute().Subscribe();

        fixture.Vm.SelectedFolder.Should().NotBeNull();
        fixture.Vm.SelectedFolder!.SteamId.Should().Be("76561198000000002");
        fixture.Vm.SelectedFile.Should().NotBeNull();
        fixture.Vm.SelectedFile!.Name.Should().Be("ER0000.co2");
    }

    [Fact]
    public void QuarantineIsDisabledWithTheReasonWhileSteamRuns()
    {
        using var fixture = new Fixture(Provider, steamRunning: true);
        fixture.Write("ER0000.sl2", "vanilla");
        fixture.Vm.RefreshCommand.Execute().Subscribe();

        fixture.Vm.SelectedFile = fixture.Vm.Files.Single(f => f.Name == "ER0000.sl2");

        fixture.Vm.IsSteamRunning.Should().BeTrue();
        fixture.Vm.CanQuarantine.Should().BeFalse();
        fixture.Vm.QuarantineDisabledReason.Should().Contain("Close Steam");
        fixture.Vm.QuarantineCommand.CanExecute.FirstAsync().Wait().Should().BeFalse();
    }

    [Fact]
    public void QuarantineIsEnabledForTheVanillaSaveOnceSteamIsClosed()
    {
        using var fixture = new Fixture(Provider, steamRunning: true);
        var save = fixture.Write("ER0000.sl2", "vanilla");
        fixture.Vm.RefreshCommand.Execute().Subscribe();
        fixture.Vm.SelectedFile = fixture.Vm.Files.Single(f => f.Name == "ER0000.sl2");

        fixture.Tracker.IsSteamRunning().Returns(false);
        fixture.Vm.RefreshCommand.Execute().Subscribe();

        fixture.Vm.CanQuarantine.Should().BeTrue();
        fixture.Vm.QuarantineDisabledReason.Should().BeEmpty();

        fixture.Vm.QuarantineCommand.Execute().Subscribe();
        File.Exists(save).Should().BeFalse();
        fixture.Vm.Message.Should().Contain("Moved ER0000.sl2");
    }

    [Theory]
    [InlineData("ER0000.co2")]
    [InlineData("ER0000_modded")]
    public void QuarantineIsDisabledForEveryFileButTheVanillaSave(string name)
    {
        using var fixture = new Fixture(Provider, steamRunning: false);
        fixture.Write(name, "x");
        fixture.Vm.RefreshCommand.Execute().Subscribe();

        fixture.Vm.SelectedFile = fixture.Vm.Files.Single(f => f.Name == name);

        fixture.Vm.CanQuarantine.Should().BeFalse();
        fixture.Vm.QuarantineDisabledReason.Should().Contain("Only the vanilla save");
    }

    [Fact]
    public void TheQuarantineReasonsNameTheGamesVanillaSave()
    {
        using var fixture = new Fixture(Provider, steamRunning: false);
        fixture.Write("ER0000.co2", "coop");
        fixture.Vm.RefreshCommand.Execute().Subscribe();

        fixture.Vm.QuarantineDisabledReason.Should().Be("Select the vanilla save (ER0000.sl2) first.");

        fixture.Vm.SelectedFile = fixture.Vm.Files.Single();
        fixture.Vm.QuarantineDisabledReason.Should().Be("Only the vanilla save (ER0000.sl2) can be quarantined.");
    }

    [Fact]
    public void BackupIsRefusedForAFileTheModuleDoesNotKnow()
    {
        using var fixture = new Fixture(Provider, steamRunning: false);
        fixture.Write("ER0000_modded", "x");
        fixture.Vm.RefreshCommand.Execute().Subscribe();

        fixture.Vm.SelectedFile = fixture.Vm.Files.Single();

        fixture.Vm.BackupCommand.CanExecute.FirstAsync().Wait().Should().BeFalse();
    }

    [Fact]
    public void BackupListsTheCopyAndRestoreAsksBeforeWriting()
    {
        using var fixture = new Fixture(Provider, steamRunning: false);
        var save = fixture.Write("ER0000.co2", "good");
        fixture.Vm.RefreshCommand.Execute().Subscribe();
        fixture.Vm.SelectedFile = fixture.Vm.Files.Single();
        fixture.Vm.BackupCommand.Execute().Subscribe();
        fixture.Vm.Backups.Should().ContainSingle().Which.SaveName.Should().Be("ER0000.co2");

        File.WriteAllText(save, "worse");
        fixture.Vm.SelectedBackup = fixture.Vm.Backups.Single();
        fixture.Vm.RestoreCommand.Execute().Subscribe();

        fixture.Vm.RestorePrompt.Should().Contain("ER0000.co2").And.Contain("pre-restore");
        File.ReadAllText(save).Should().Be("worse", "restoring waits for the answer");

        fixture.Vm.CancelRestoreCommand.Execute().Subscribe();
        fixture.Vm.RestorePrompt.Should().BeEmpty();
        File.ReadAllText(save).Should().Be("worse");

        fixture.Vm.SelectedBackup = fixture.Vm.Backups.Single();
        fixture.Vm.RestoreCommand.Execute().Subscribe();
        fixture.Vm.ConfirmRestoreCommand.Execute().Subscribe();

        File.ReadAllText(save).Should().Be("good");
        fixture.Vm.RestorePrompt.Should().BeEmpty();
        fixture.Vm.Backups.Should().HaveCount(2, "the replaced save was kept as a pre-restore copy");
        fixture.Vm.Backups.Select(b => b.Label).Should().Contain("pre-restore");
    }

    [Fact]
    public void ATaintedLedgerShowsUpAndResolveClearsIt()
    {
        using var fixture = new Fixture(Provider, steamRunning: false);
        fixture.Write("ER0000.sl2", "vanilla");
        var ledgerFolder = Path.Combine(fixture.DataRoot, "EldenRing", EldenRingPaths.SteamAppId.ToString(), "saves");
        Directory.CreateDirectory(ledgerFolder);
        var entry = new { folder = fixture.SaveFolder, hash = "", copyPath = Path.Combine(ledgerFolder, "session", "s", Account), tainted = true, stamp = "s" };
        File.WriteAllText(Path.Combine(ledgerFolder, "ledger.json"), JsonSerializer.Serialize(new[] { entry }));

        fixture.Vm.RefreshCommand.Execute().Subscribe();

        fixture.Vm.HasTaintedSave.Should().BeTrue();
        fixture.Vm.TaintedMessage.Should().Contain(fixture.SaveFolder);

        fixture.Vm.ResolveCommand.Execute().Subscribe();

        fixture.Vm.HasTaintedSave.Should().BeFalse();
        File.ReadAllText(Path.Combine(ledgerFolder, "ledger.json")).Should().NotContain("tainted\": true");
    }

    [Fact]
    public void ANeverWrittenSaveFolderListsNothingWithoutAProblem()
    {
        using var fixture = new Fixture(Provider, steamRunning: false);
        Directory.Delete(fixture.SaveFolder);

        fixture.Vm.RefreshCommand.Execute().Subscribe();

        fixture.Vm.Folders.Should().BeEmpty();
        fixture.Vm.Problem.Should().BeEmpty();
    }
}
