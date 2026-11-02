using System.Collections.Immutable;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.FolderCase;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests.FolderCase;

public sealed class CaseTwinMergerTests : IDisposable
{
    private readonly TemporaryFileManager _temp = new(FileSystem.Shared);
    private readonly AbsolutePath _game;
    private readonly GameInstallation _installation;
    private readonly IFileStore _store = Substitute.For<IFileStore>();
    private readonly Dictionary<GamePath, FileIdentity> _managed = new();

    public CaseTwinMergerTests()
    {
        _game = _temp.CreateFolder().Path;
        var game = Substitute.For<IGame>();
        game.DisplayName.Returns("Fixture Game");
        var locatorResult = new GameLocatorResult
        {
            Game = game,
            Path = _game,
            Store = GameStore.Steam,
            StoreIdentifier = "1",
            LocatorIds = [LocatorId.From("1")],
            Locator = Substitute.For<IGameLocator>(),
        };
        _installation = new GameInstallation(locatorResult, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, _game)));
        _store.HaveFile(Arg.Any<FileIdentity>()).Returns(false);
    }

    public void Dispose() => _temp.Dispose();

    private static GamePath G(string path) => new(LocationId.Game, RelativePath.FromUnsanitizedInput(path));

    private string Write(string relative, string content, DateTime? modified = null)
    {
        var file = _game.Combine(relative);
        file.Parent.CreateDirectory();
        File.WriteAllText(file.GetFullPath(), content);
        if (modified is { } time) File.SetLastWriteTimeUtc(file.GetFullPath(), time);
        return file.GetFullPath();
    }

    private bool Exists(string relative) => File.Exists(_game.Combine(relative).GetFullPath());
    private bool DirExists(string relative) => Directory.Exists(_game.Combine(relative).GetFullPath());

    private void Manage(params string[] paths)
    {
        foreach (var path in paths) _managed[G(path)] = default;
    }

    private static FileIdentity Hash(string content) => FileIdentity.XxHash3(Encoding.UTF8.GetBytes(content).xxHash3());

    private FolderSpellings Spellings(string[] fixedFolders, params string[] loadout)
    {
        var folders = Directory.EnumerateDirectories(_game.GetFullPath(), "*", SearchOption.AllDirectories)
            .Select(d => G(Path.GetRelativePath(_game.GetFullPath(), d))).ToArray();
        var files = Directory.EnumerateFiles(_game.GetFullPath(), "*", SearchOption.AllDirectories)
            .Select(f => (G(Path.GetRelativePath(_game.GetFullPath(), f)), false)).ToArray();
        return FolderSpellings.Build(new FolderSpellingSources(fixedFolders, AllTiers: true, folders, files, loadout.Select(G).ToArray()));
    }

    private async Task<MergeResult> Merge(FolderSpellings spellings, IGamePathFilter? filter = null, Func<string, string, bool>? sameDirectory = null)
    {
        var merger = sameDirectory is null
            ? new CaseTwinMerger(MultiHashFileHasher.Instance, _store, NullLogger.Instance, filter ?? GamePathFilters.Empty)
            : new CaseTwinMerger(MultiHashFileHasher.Instance, _store, NullLogger.Instance, filter ?? GamePathFilters.Empty, sameDirectory);
        return await merger.MergeTwins(_installation, spellings, _managed, CancellationToken.None);
    }

    [Fact]
    public async Task RenamesALoneVariant()
    {
        Write("mods/a/x.ws", "x");
        Manage("Mods/a/x.ws");

        await Merge(FolderSpellings.Fixed(["Mods"]));

        Directory.GetDirectories(_game.GetFullPath()).Select(Path.GetFileName).Should().Equal("Mods");
        Exists("Mods/a/x.ws").Should().BeTrue();
    }

    [Fact]
    public async Task MovesIntoExistingCanonical_RemovesEmptiedVariant()
    {
        Write("Data/Interface/vanilla.swf", "v");
        Write("Data/Interface/more.swf", "m");
        Write("Data/interface/a.swf", "a");
        Write("Data/interface/sub/b.swf", "b");
        Manage("Data/interface/a.swf", "Data/interface/sub/b.swf");

        var result = await Merge(Spellings([], "Data/interface/a.swf", "Data/interface/sub/b.swf"));

        Exists("Data/Interface/a.swf").Should().BeTrue();
        Exists("Data/Interface/sub/b.swf").Should().BeTrue();
        DirExists("Data/interface").Should().BeFalse();
        result.Moved.Should().Contain(G("Data/Interface/a.swf"));
    }

    [Fact]
    public async Task NestedTwins_MergeAtEveryDepth()
    {
        Write("Data/Scripts/Source/a.psc", "a");
        Write("Data/scripts/source/b.psc", "b");
        Write("DATA/Scripts/SOURCE/c.psc", "c");
        Manage("Data/scripts/source/b.psc", "DATA/Scripts/SOURCE/c.psc", "Data/Scripts/Source/a.psc");

        await Merge(Spellings([], "Data/scripts/source/b.psc", "DATA/Scripts/SOURCE/c.psc", "Data/Scripts/Source/a.psc"));

        var folders = Directory.EnumerateDirectories(_game.GetFullPath(), "*", SearchOption.AllDirectories).Select(d => Path.GetRelativePath(_game.GetFullPath(), d)).ToArray();
        folders.Select(f => f.ToUpperInvariant()).Should().OnlyHaveUniqueItems();
        Directory.EnumerateFiles(_game.GetFullPath(), "*", SearchOption.AllDirectories).Should().HaveCount(3);
    }

    [Fact]
    public async Task MovedSubBesideExistingSub_MergedInSameWalk()
    {
        Write("Data/Interface/sub/one.swf", "1");
        Write("Data/interface/Sub/two.swf", "2");
        Manage("Data/Interface/sub/one.swf", "Data/interface/Sub/two.swf");

        await Merge(Spellings([], "Data/Interface/sub/one.swf", "Data/interface/Sub/two.swf"));

        Directory.EnumerateDirectories(_game.GetFullPath(), "*", SearchOption.AllDirectories).Select(d => Path.GetRelativePath(_game.GetFullPath(), d).ToUpperInvariant())
            .Should().OnlyHaveUniqueItems();
        Directory.EnumerateFiles(_game.GetFullPath(), "*", SearchOption.AllDirectories).Should().HaveCount(2);
    }

    [Fact]
    public async Task SameBytes_DropsVariant()
    {
        Write("Mods/a/x.ws", "same");
        Write("mods/a/x.ws", "same");
        Manage("Mods/a/x.ws");

        var result = await Merge(Spellings(["Mods"], "Mods/a/x.ws"));

        Exists("Mods/a/x.ws").Should().BeTrue();
        DirExists("mods").Should().BeFalse();
        result.Deduplicated.Should().Contain(G("Mods/a/x.ws"));
    }

    [Fact]
    public async Task DifferentBytes_LeavesBoth_Reports()
    {
        Write("Mods/a/x.ws", "canonical");
        Write("mods/a/x.ws", "variant");
        Manage("Mods/a/x.ws");

        var result = await Merge(Spellings(["Mods"], "Mods/a/x.ws"));

        File.ReadAllText(_game.Combine("Mods/a/x.ws").GetFullPath()).Should().Be("canonical");
        File.ReadAllText(_game.Combine("mods/a/x.ws").GetFullPath()).Should().Be("variant");
        result.LeftInPlace.Should().Contain(G("mods/a/x.ws"));
        result.NeedsFreshRead.Should().Contain(G("Mods/a/x.ws"));
    }

    [Fact]
    public async Task AppOwnedCopy_DeletedOnlyWhenArchived()
    {
        Write("Mods/a/x.ws", "other");
        Write("mods/a/x.ws", "app copy");
        _managed[G("Mods/a/x.ws")] = Hash("app copy");

        var notArchived = await Merge(Spellings(["Mods"], "Mods/a/x.ws"));
        notArchived.LeftInPlace.Should().Contain(G("mods/a/x.ws"));
        File.ReadAllText(_game.Combine("mods/a/x.ws").GetFullPath()).Should().Be("app copy");

        _store.HaveFile(Hash("app copy")).Returns(true);
        var archived = await Merge(Spellings(["Mods"], "Mods/a/x.ws"));

        File.ReadAllText(_game.Combine("Mods/a/x.ws").GetFullPath()).Should().Be("other", "the other copy keeps the place");
        DirExists("mods").Should().BeFalse();
        archived.NeedsFreshRead.Should().Contain(G("Mods/a/x.ws"));
    }

    [Fact]
    public async Task FileVsFolder_LeavesBoth()
    {
        Write("Mods/a", "a file");
        Write("mods/a/inner.ws", "in a folder");
        Manage("mods/a/inner.ws");

        var result = await Merge(Spellings(["Mods"], "mods/a/inner.ws"));

        Exists("Mods/a").Should().BeTrue();
        Exists("mods/a/inner.ws").Should().BeTrue();
        result.LeftInPlace.Should().Contain(G("mods/a"));
    }

    [Fact]
    public async Task SameDirectoryInode_RenamesOnly_DeletesNothing()
    {
        Write("Mods/a/x.ws", "x");
        Write("mods/a/y.ws", "y");
        Manage("Mods/a/x.ws");

        await Merge(Spellings(["Mods"], "Mods/a/x.ws"), sameDirectory: (_, _) => true);

        Exists("Mods/a/x.ws").Should().BeTrue();
        Exists("mods/a/y.ws").Should().BeTrue("a folder that is the canonical one under another name is never emptied or deleted");
    }

    [Fact]
    public async Task SymlinkedVariant_Skipped()
    {
        var target = _temp.CreateFolder().Path;
        File.WriteAllText(target.Combine("y.ws").GetFullPath(), "y");
        Write("Mods/a/x.ws", "x");
        File.CreateSymbolicLink(_game.Combine("mods").GetFullPath(), target.GetFullPath());
        Manage("Mods/a/x.ws");

        var result = await Merge(Spellings(["Mods"], "Mods/a/x.ws"));

        File.Exists(target.Combine("y.ws").GetFullPath()).Should().BeTrue();
        Directory.Exists(_game.Combine("Mods/y.ws").GetFullPath()).Should().BeFalse();
        result.Moved.Should().BeEmpty();
        new FileInfo(_game.Combine("mods").GetFullPath()).LinkTarget.Should().NotBeNull();
    }

    [Fact]
    public async Task FilteredSavesTwin_Untouched()
    {
        Write("Saves/a.sav", "a");
        Write("saves/b.sav", "b");
        Manage("Saves/a.sav", "saves/b.sav");

        var result = await Merge(Spellings([], "Saves/a.sav", "saves/b.sav"), GamePathFilters.Create(p => p.Path.ToString().StartsWith("saves", StringComparison.OrdinalIgnoreCase)));

        Exists("Saves/a.sav").Should().BeTrue();
        Exists("saves/b.sav").Should().BeTrue();
        result.Moved.Should().BeEmpty();
    }

    [Fact]
    public async Task UnmanagedOnlyTwin_Untouched_Reported()
    {
        Write("Data/Own/a.txt", "a");
        Write("Data/own/b.txt", "b");

        var result = await Merge(Spellings([]));

        Exists("Data/Own/a.txt").Should().BeTrue();
        Exists("Data/own/b.txt").Should().BeTrue();
        result.LeftInPlace.Should().Contain(G("Data/Own")).And.Contain(G("Data/own"));
    }

    [Fact]
    public async Task ReadOnlyFolder_ThrowsNamingTheFolder()
    {
        if (Environment.UserName == "root") return;
        Write("mods/a/x.ws", "x");
        Manage("Mods/a/x.ws");
        var parent = _game.GetFullPath();
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var act = async () => await Merge(FolderSpellings.Fixed(["Mods"]));
            (await act.Should().ThrowAsync<SynchronizerPreflightException>()).Which.Message.Should().Contain("mods");
        }
        finally
        {
            File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task RenameKeepsSizeAndModificationTime()
    {
        var time = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Write("Data/Interface/v.swf", "v");
        Write("Data/interface/a.swf", "payload", time);
        Manage("Data/interface/a.swf");

        await Merge(Spellings([], "Data/interface/a.swf"));

        var moved = new FileInfo(_game.Combine("Data/Interface/a.swf").GetFullPath());
        moved.Length.Should().Be("payload".Length);
        moved.LastWriteTimeUtc.Should().Be(time);
    }
}
