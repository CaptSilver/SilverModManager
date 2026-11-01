using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Heal;
using SilverModManager.Games.FromSoft.Tests.Build;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Saves;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Games.FromSoft.Tests.Heal;

public sealed class RegulationRestoreTests : IDisposable
{
    private const string Version = BuildIds.RegulationVersion;
    private static readonly byte[] Vanilla = Real(1);
    private static readonly byte[] Modded = Real(2);

    private readonly string _root = Directory.CreateTempSubdirectory("regulation-restore").FullName;
    private readonly IGameLaunchSession _session = Substitute.For<IGameLaunchSession>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 9, 30, 15, TimeSpan.Zero));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string BaselineFolder => Path.Combine(_root, "baselines");
    private string SetAsideFolder => Path.Combine(_root, "set-aside");
    private string GameRoot => Path.Combine(_root, "install");
    private string RegulationOnDisk => Path.Combine(GameRoot, EldenRingPaths.GameSubfolder, "regulation.bin");

    private static byte[] Real(byte fill)
        => RegulationFixture.BuildFromEntries([(1, "a.param", Enumerable.Repeat(fill, 16).ToArray())], Version, EldenRingGame.Definition.Formats);

    private static Sha1Value Sha1Of(byte[] blob) => Sha1Value.From(SHA1.HashData(blob));

    /// <summary>What Steam's manifest for the installed build lists, as paths under the install folder.</summary>
    private sealed class ListedFiles(params byte[][] regulations) : IManifestVouching
    {
        public ManifestMatch? Find(GameInstallation installation, Sha1Value hash) => null;

        public IReadOnlyList<ManifestFile>? InstalledFiles(GameInstallation installation)
            => regulations.Select(r => new ManifestFile($"{EldenRingPaths.GameSubfolder}/regulation.bin", (ulong)r.Length, Sha1Of(r))).ToArray();
    }

    private sealed class NoManifest : IManifestVouching
    {
        public ManifestMatch? Find(GameInstallation installation, Sha1Value hash) => null;

        public IReadOnlyList<ManifestFile>? InstalledFiles(GameInstallation installation) => null;
    }

    private RegulationBaselines Baselines() => new(() => FileSystem.Shared.FromUnsanitizedFullPath(BaselineFolder), null, _time);

    /// <summary>An install whose regulation is the modded one, with the vanilla kept as the build's baseline.</summary>
    private GameInstallation ModdedInstallWithBaseline()
    {
        var game = HealFixtures.Game(GameRoot, Vanilla);
        Baselines().Adopt(game, HealFixtures.Identity(Vanilla)).Should().NotBeNull();
        HealFixtures.WriteRegulation(GameRoot, Modded);
        return game;
    }

    private RegulationRestore Restorer(IManifestVouching vouching)
        => new(Baselines(), vouching, _session, () => FileSystem.Shared.FromUnsanitizedFullPath(SetAsideFolder), _time, NullLogger.Instance);

    private string[] LeftInGameFolder() => Directory.GetFiles(Path.GetDirectoryName(RegulationOnDisk)!).Select(Path.GetFileName).OfType<string>().Order().ToArray();

    [Fact]
    public void RestoreWritesTheVouchedBytesOverTheRegulation()
    {
        var game = ModdedInstallWithBaseline();

        var restored = Restorer(new ListedFiles(Vanilla)).Restore(game, null);

        restored.Changed.Should().BeTrue();
        File.ReadAllBytes(RegulationOnDisk).Should().Equal(Vanilla);
    }

    [Fact]
    public void RestoreSetsTheModifiedFileAsideUnderANameThatSaysWhatItWas()
    {
        var game = ModdedInstallWithBaseline();

        var restored = Restorer(new ListedFiles(Vanilla)).Restore(game, null);

        var kept = restored.SetAside!.Value;
        File.ReadAllBytes(kept.GetFullPath()).Should().Equal(Modded);
        kept.Parent.GetFullPath().Should().Be(SetAsideFolder);
        kept.FileName.Should().StartWith("regulation-modified-").And.EndWith(".bin").And.Contain("20261101T093015Z");
    }

    [Fact]
    public void RestoreLeavesNoTemporaryFileInTheGameFolder()
    {
        var game = ModdedInstallWithBaseline();

        Restorer(new ListedFiles(Vanilla)).Restore(game, null);

        LeftInGameFolder().Should().Equal("regulation.bin");
    }

    [Fact]
    public void RestoreRefusesWithNoBaselineAndChangesNothing()
    {
        var game = HealFixtures.Game(GameRoot, Modded);

        var act = () => Restorer(new ListedFiles(Vanilla)).Restore(game, null);

        act.Should().Throw<RegulationRestoreRefusedException>();
        File.ReadAllBytes(RegulationOnDisk).Should().Equal(Modded);
        Directory.Exists(SetAsideFolder).Should().BeFalse();
        LeftInGameFolder().Should().Equal("regulation.bin");
    }

    [Fact]
    public void RestoreRefusesWhileTheGameIsRunningAndChangesNothing()
    {
        var game = ModdedInstallWithBaseline();
        _session.IsRunning(Arg.Any<GameInstallation>()).Returns(true);

        var act = () => Restorer(new ListedFiles(Vanilla)).Restore(game, null);

        act.Should().Throw<RegulationRestoreRefusedException>().WithMessage("*running*");
        File.ReadAllBytes(RegulationOnDisk).Should().Equal(Modded);
        Directory.Exists(SetAsideFolder).Should().BeFalse();
    }

    [Fact]
    public void ABaselineWhoseBytesNoLongerHashAsRecordedIsNeverWritten()
    {
        var game = ModdedInstallWithBaseline();
        File.WriteAllBytes(Baselines().Find(Version)!.Value.GetFullPath(), "not the file that was kept"u8.ToArray());
        var restorer = Restorer(new ListedFiles(Vanilla));

        var act = () => restorer.Restore(game, null);

        act.Should().Throw<RegulationRestoreRefusedException>().WithMessage("*no longer matches*");
        File.ReadAllBytes(RegulationOnDisk).Should().Equal(Modded);
        Directory.Exists(SetAsideFolder).Should().BeFalse();
        LeftInGameFolder().Should().Equal("regulation.bin");
    }

    [Fact]
    public void TheButtonsGoByTheRecordedHashesAndTheManifestTheCallerPassed()
    {
        var game = ModdedInstallWithBaseline();
        // Only the restore itself reads the kept file; a changed one is refused there.
        File.WriteAllBytes(Baselines().Find(Version)!.Value.GetFullPath(), "changed since it was kept"u8.ToArray());
        var vouching = Substitute.For<IManifestVouching>();

        var actions = Restorer(vouching).Actions(game, null, regulationDiffers: true, otherFilesDiffer: false, new ListedFiles(Vanilla).InstalledFiles(game));

        actions.Should().ContainSingle().Which.Id.Should().Be(RegulationRestore.RestoreActionId);
        vouching.DidNotReceiveWithAnyArgs().InstalledFiles(default!);
    }

    [Fact]
    public void ABaselineTheInstalledManifestDoesNotListIsNotUsed()
    {
        var game = ModdedInstallWithBaseline();
        var olderBuild = Real(9);

        Restorer(new ListedFiles(olderBuild)).Candidate(game, null).Should().BeNull();
    }

    [Fact]
    public void WithNoReadableManifestTheStampedBuildsBaselineServesWhenTheExecutableDidNotChange()
    {
        var game = ModdedInstallWithBaseline();
        File.WriteAllBytes(Path.Combine(GameRoot, EldenRingPaths.GameSubfolder, "eldenring.exe"), FakePe.WithVersion(new Version(2, 7, 0, 0)));
        var stamped = HealFixtures.Identity(Vanilla);

        var restorer = Restorer(new NoManifest());

        restorer.Candidate(game, stamped).Should().NotBeNull();
        restorer.Candidate(game, stamped with { Exe = "2.6.0.0" }).Should().BeNull();
        restorer.Candidate(game, stamped with { SteamBuildId = "1" }).Should().BeNull();
        restorer.Candidate(game, null).Should().BeNull();
    }

    [Fact]
    public void ARegulationThatAlreadyIsTheVouchedFileIsLeftAlone()
    {
        var game = HealFixtures.Game(GameRoot, Vanilla);
        Baselines().Adopt(game, HealFixtures.Identity(Vanilla));

        var restored = Restorer(new ListedFiles(Vanilla)).Restore(game, null);

        restored.Should().Be(new RegulationRestored(false, null));
        Directory.Exists(SetAsideFolder).Should().BeFalse();
    }

    [Fact]
    public void ARestoreOfAMissingRegulationJustWritesIt()
    {
        var game = ModdedInstallWithBaseline();
        File.Delete(RegulationOnDisk);

        var restored = Restorer(new ListedFiles(Vanilla)).Restore(game, null);

        restored.SetAside.Should().BeNull();
        File.ReadAllBytes(RegulationOnDisk).Should().Equal(Vanilla);
    }

    [Fact]
    public void ABaselineAndATamperedRegulationOffersTheRestoreAlone()
    {
        var game = ModdedInstallWithBaseline();

        var actions = Restorer(new ListedFiles(Vanilla)).Actions(game, null, regulationDiffers: true, otherFilesDiffer: false);

        actions.Should().ContainSingle().Which.Id.Should().Be(RegulationRestore.RestoreActionId);
        actions.Single().Label.Should().Be("Restore the game's own regulation");
    }

    [Fact]
    public void WithoutABaselineTheButtonIsSteamsVerifyForTheAppId()
    {
        var game = HealFixtures.Game(GameRoot, Modded);

        var actions = Restorer(new ListedFiles(Vanilla)).Actions(game, null, regulationDiffers: true, otherFilesDiffer: false);

        var action = actions.Should().ContainSingle().Which;
        action.Id.Should().Be(DiagnosticActions.VerifyGameFilesInSteamId);
        action.Label.Should().Be("Verify the game's files in Steam");
        action.Arguments.Should().Equal("1245620");
    }

    [Fact]
    public void FilesOtherThanTheRegulationGetOnlyTheVerifyButton()
    {
        var game = ModdedInstallWithBaseline();

        var actions = Restorer(new ListedFiles(Vanilla)).Actions(game, null, regulationDiffers: false, otherFilesDiffer: true);

        actions.Should().ContainSingle().Which.Id.Should().Be(DiagnosticActions.VerifyGameFilesInSteamId);
    }

    [Fact]
    public void ARegulationWithOtherChangedFilesGetsBothButtons()
    {
        var game = ModdedInstallWithBaseline();

        var actions = Restorer(new ListedFiles(Vanilla)).Actions(game, null, regulationDiffers: true, otherFilesDiffer: true);

        actions.Select(a => a.Id).Should().Equal(RegulationRestore.RestoreActionId, DiagnosticActions.VerifyGameFilesInSteamId);
    }

    [Fact]
    public void NothingDifferingOffersNothing()
    {
        var game = ModdedInstallWithBaseline();

        Restorer(new ListedFiles(Vanilla)).Actions(game, null, regulationDiffers: false, otherFilesDiffer: false).Should().BeEmpty();
    }
}
