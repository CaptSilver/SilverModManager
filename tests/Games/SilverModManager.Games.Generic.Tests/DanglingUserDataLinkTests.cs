using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.App.UI.DiagnosticSystem;
using NexusMods.Games.Generic.Diagnostics;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NSubstitute;
using Xunit;

namespace NexusMods.Games.Generic.Tests;

/// <summary>
/// The save and settings folders a game keeps outside its install folder can be turned into links by other
/// tools. These tests build the links in a temp directory and never touch a real prefix.
/// </summary>
public class DanglingUserDataLinkTests : IDisposable
{
    private const ushort DanglingLinkId = 5;

    private readonly string _root = Directory.CreateTempSubdirectory("dangling-links").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_root, ..parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private string File_(string folder, string name, string contents = "data")
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, contents);
        return path;
    }

    /// <summary>A link at <paramref name="name"/> whose target does not exist.</summary>
    private string DanglingLink(string parent, string name)
    {
        var path = Path.Combine(parent, name);
        Directory.CreateSymbolicLink(path, Path.Combine(_root, "gone", Guid.NewGuid().ToString("N")));
        return path;
    }

    private static string Details(Diagnostic diagnostic, IServiceProvider provider)
        => diagnostic.FormatDetails(provider.GetRequiredService<IDiagnosticWriter>());

    private IServiceProvider Provider { get; } = new ServiceCollection().AddLogging().AddSingleton(Substitute.For<NexusMods.MnemonicDB.Abstractions.IConnection>()).AddDiagnosticWriter().BuildServiceProvider();

    private IEnumerable<Diagnostic> Diagnose(params string[] folders)
        => UserDataLinkEmitter.DiagnoseFolders("Test Game", folders);

    [Fact]
    public void ADanglingFolderLinkIsCritical_NamingThePathAndTheMissingTarget()
    {
        var parent = Dir("saves");
        var link = DanglingLink(parent, "Game");
        var target = new DirectoryInfo(link).LinkTarget!;

        var diagnostic = Diagnose(link).Should().ContainSingle().Which;

        diagnostic.Id.Should().Be(new DiagnosticId("NexusMods.Games.Generic", DanglingLinkId));
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Critical);
        var details = Details(diagnostic, Provider);
        details.Should().Contain(link);
        details.Should().Contain(target);
    }

    [Fact]
    public void ADanglingLinkInsideAFolderIsFlagged()
    {
        var folder = Dir("saves", "Game");
        var child = DanglingLink(folder, "slot1");

        var diagnostic = Diagnose(folder).Should().ContainSingle().Which;

        Details(diagnostic, Provider).Should().Contain(child);
    }

    [Fact]
    public void ALinkWhoseTargetExistsIsNotFlagged()
    {
        var real = Dir("elsewhere");
        var parent = Dir("saves");
        Directory.CreateSymbolicLink(Path.Combine(parent, "Game"), real);
        var folder = Dir("saves2", "Game");
        Directory.CreateSymbolicLink(Path.Combine(folder, "slot1"), real);

        Diagnose(Path.Combine(parent, "Game"), folder).Should().BeEmpty();
    }

    [Fact]
    public void ARealFolderAndAMissingFolderAreNotFlagged()
    {
        var folder = Dir("saves", "Game");
        File_(folder, "a.dat");

        Diagnose(folder, Path.Combine(_root, "never-created")).Should().BeEmpty();
    }

    [Fact]
    public void OneBackupWithFiles_OffersARestoreActionNamingBothPaths()
    {
        var parent = Dir("saves");
        var link = DanglingLink(parent, "Game");
        var backup = Dir("saves", "Game.fluorine-backup-1234");
        File_(backup, "a.dat");

        var action = Diagnose(link).Single().Actions.Should().ContainSingle().Which;

        action.Id.Should().Be(DiagnosticActions.RestoreFolderFromBackupId);
        action.Label.Should().Be("Restore Game.fluorine-backup-1234");
        action.Arguments.Should().Equal(link, backup);
    }

    [Fact]
    public void SeveralBackups_AreListedWithNoAction()
    {
        var parent = Dir("saves");
        var link = DanglingLink(parent, "Game");
        foreach (var name in new[] { "Game.backup-a", "Game.other-backup-b" })
            File_(Dir("saves", name), "a.dat");

        var diagnostic = Diagnose(link).Single();

        diagnostic.Actions.Should().BeEmpty();
        var details = Details(diagnostic, Provider);
        details.Should().Contain("Game.backup-a").And.Contain("Game.other-backup-b");
    }

    [Fact]
    public void AnEmptyBackup_OffersNoAction()
    {
        var parent = Dir("saves");
        var link = DanglingLink(parent, "Game");
        Dir("saves", "Game.backup-a");

        Diagnose(link).Single().Actions.Should().BeEmpty();
    }

    [Fact]
    public void ABackupThatIsItselfALink_OffersNoAction()
    {
        var parent = Dir("saves");
        var link = DanglingLink(parent, "Game");
        var real = Dir("elsewhere");
        File_(real, "a.dat");
        Directory.CreateSymbolicLink(Path.Combine(parent, "Game.backup-a"), real);

        Diagnose(link).Single().Actions.Should().BeEmpty();
    }

    [Fact]
    public void FoldersToCheck_ResolvesDeclaredFoldersThroughTheInstallationsFileSystem_AndAddsMappedOnes()
    {
        var fileSystem = new InMemoryFileSystem(OSInformation.FakeUnix);
        var gamePath = fileSystem.FromUnsanitizedFullPath("/games/Test");
        var savesPath = fileSystem.FromUnsanitizedFullPath("/data/saves");
        var game = Substitute.For<IGameData, IGameUserDataFolders>();
        ((IGameUserDataFolders)game).UserDataFolders.Returns([new UserDataFolder(KnownPath.HomeDirectory, "Saved Games/Studio/Game")]);
        var result = new GameLocatorResult
        {
            Game = game,
            Path = gamePath,
            Store = GameStore.Steam,
            StoreIdentifier = "42",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
        };
        var installation = new GameInstallation(result, GameLocations.Create(
            ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, gamePath).Add(LocationId.Saves, savesPath)));

        var folders = UserDataLinkEmitter.FoldersToCheck(installation);

        folders.Should().BeEquivalentTo(
            fileSystem.GetKnownPath(KnownPath.HomeDirectory).Combine("Saved Games/Studio/Game").GetFullPath(),
            savesPath.GetFullPath());
    }

    [Fact]
    public void AMappedLocationThatIsADanglingLink_IsDiagnosedThroughTheInstallation()
    {
        var link = DanglingLink(Dir("saves"), "Game");
        var gamePath = FileSystem.Shared.FromUnsanitizedFullPath(Dir("game"));
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game");
        var result = new GameLocatorResult
        {
            Game = game,
            Path = gamePath,
            Store = GameStore.Steam,
            StoreIdentifier = "42",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
        };
        var installation = new GameInstallation(result, GameLocations.Create(
            ImmutableDictionary<LocationId, AbsolutePath>.Empty
                .Add(LocationId.Game, gamePath)
                .Add(LocationId.Saves, FileSystem.Shared.FromUnsanitizedFullPath(link))));

        var diagnostic = UserDataLinkEmitter.DiagnoseInstallation(installation).Should().ContainSingle().Which;

        Details(diagnostic, Provider).Should().Contain(link);
    }

    #region restore

    private (string Link, string Backup) LinkAndBackup()
    {
        var link = DanglingLink(Dir("saves"), "Game");
        var backup = Dir("saves", "Game.backup-a");
        File_(backup, "a.dat", "precious");
        return (link, backup);
    }

    [Fact]
    public void Restore_SwapsTheLinkForTheBackup_KeepingFileContents()
    {
        var (link, backup) = LinkAndBackup();

        SavedFolderRestore.Restore(link, backup);

        new DirectoryInfo(link).LinkTarget.Should().BeNull();
        File.ReadAllText(Path.Combine(link, "a.dat")).Should().Be("precious");
        Directory.Exists(backup).Should().BeFalse();
    }

    [Fact]
    public void Restore_RefusesWhenTheLinkNowResolves()
    {
        var (link, backup) = LinkAndBackup();
        File.Delete(link);
        Directory.CreateSymbolicLink(link, Dir("now-exists"));

        var act = () => SavedFolderRestore.Restore(link, backup);

        act.Should().Throw<InvalidOperationException>();
        new DirectoryInfo(link).LinkTarget.Should().NotBeNull();
        File.ReadAllText(Path.Combine(backup, "a.dat")).Should().Be("precious");
    }

    [Fact]
    public void Restore_RefusesWhenThePathIsARealFolder()
    {
        var (link, backup) = LinkAndBackup();
        File.Delete(link);
        File_(Dir("saves", "Game"), "mine.dat");

        var act = () => SavedFolderRestore.Restore(link, backup);

        act.Should().Throw<InvalidOperationException>();
        File.Exists(Path.Combine(link, "mine.dat")).Should().BeTrue();
        File.Exists(Path.Combine(backup, "a.dat")).Should().BeTrue();
    }

    [Fact]
    public void Restore_RefusesWhenTheBackupIsALink()
    {
        var (link, backup) = LinkAndBackup();
        Directory.Delete(backup, recursive: true);
        var real = Dir("elsewhere");
        File_(real, "a.dat");
        Directory.CreateSymbolicLink(backup, real);

        var act = () => SavedFolderRestore.Restore(link, backup);

        act.Should().Throw<InvalidOperationException>();
        new DirectoryInfo(link).LinkTarget.Should().NotBeNull();
        File.Exists(Path.Combine(real, "a.dat")).Should().BeTrue();
    }

    [Fact]
    public void Restore_RefusesWhenTheBackupIsGone()
    {
        var (link, backup) = LinkAndBackup();
        Directory.Delete(backup, recursive: true);

        var act = () => SavedFolderRestore.Restore(link, backup);

        act.Should().Throw<InvalidOperationException>();
        new DirectoryInfo(link).LinkTarget.Should().NotBeNull();
    }

    [Fact]
    public void Restore_RefusesAnEmptyBackup()
    {
        var (link, backup) = LinkAndBackup();
        File.Delete(Path.Combine(backup, "a.dat"));

        var act = () => SavedFolderRestore.Restore(link, backup);

        act.Should().Throw<InvalidOperationException>();
        new DirectoryInfo(link).LinkTarget.Should().NotBeNull();
        Directory.Exists(backup).Should().BeTrue();
    }

    [Fact]
    public void Restore_RefusesABackupThatIsNotASiblingOfTheLink()
    {
        var (link, _) = LinkAndBackup();
        var stranger = Dir("elsewhere", "Game.backup-a");
        File_(stranger, "a.dat");

        var act = () => SavedFolderRestore.Restore(link, stranger);

        act.Should().Throw<InvalidOperationException>();
        new DirectoryInfo(link).LinkTarget.Should().NotBeNull();
        File.Exists(Path.Combine(stranger, "a.dat")).Should().BeTrue();
    }

    [Fact]
    public void Restore_ReportsAFolderItCannotChangeAsARefusal()
    {
        var (link, backup) = LinkAndBackup();
        var parent = Path.GetDirectoryName(link)!;
        var mode = File.GetUnixFileMode(parent);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var act = () => SavedFolderRestore.Restore(link, backup);

            act.Should().Throw<InvalidOperationException>();
            new DirectoryInfo(link).LinkTarget.Should().NotBeNull();
            File.ReadAllText(Path.Combine(backup, "a.dat")).Should().Be("precious");
        }
        finally
        {
            File.SetUnixFileMode(parent, mode);
        }
    }

    #endregion
}
