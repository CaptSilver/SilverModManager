using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using NSubstitute;

namespace SilverModManager.Platform.Proton.Tests.Processes;

public class ProcProcessTrackerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("silvermod-proc-").FullName;
    private readonly string _proc;
    private readonly string _gameDir;
    private readonly string _prefix;

    public ProcProcessTrackerTests()
    {
        _proc = Directory.CreateDirectory(Path.Combine(_root, "proc")).FullName;
        _gameDir = Directory.CreateDirectory(Path.Combine(_root, "steam", "steamapps", "common", "Test Game")).FullName;
        _prefix = Directory.CreateDirectory(Path.Combine(_root, "steam", "steamapps", "compatdata", "42", "pfx")).FullName;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ProcProcessTracker Tracker => new(_proc);

    /// <summary>
    /// Writes one fake <c>/proc/&lt;pid&gt;</c> entry. Arguments and environment entries are NUL-terminated,
    /// like the kernel's files.
    /// </summary>
    private void AddProcess(int pid, string comm, string? exe = null, string[]? argv = null, string[]? environ = null, string? cwd = null)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_proc, pid.ToString())).FullName;
        File.WriteAllText(Path.Combine(dir, "comm"), comm + "\n");
        File.WriteAllBytes(Path.Combine(dir, "cmdline"), Encoding.UTF8.GetBytes(string.Concat((argv ?? [comm]).Select(a => a + "\0"))));
        File.WriteAllBytes(Path.Combine(dir, "environ"), Encoding.UTF8.GetBytes(string.Concat((environ ?? []).Select(e => e + "\0"))));
        if (exe is not null) File.CreateSymbolicLink(Path.Combine(dir, "exe"), exe);
        if (cwd is not null) File.CreateSymbolicLink(Path.Combine(dir, "cwd"), cwd);
    }

    private GameInstallation Installation()
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game");
        var fs = FileSystem.Shared;
        var gameFolder = fs.FromUnsanitizedFullPath(_gameDir);

        var result = new GameLocatorResult
        {
            Game = game,
            Path = gameFolder,
            Store = GameStore.Steam,
            StoreIdentifier = "42",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
            WinePrefixPath = fs.FromUnsanitizedFullPath(_prefix),
        };

        var locations = GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, gameFolder));
        return new GameInstallation(result, locations);
    }

    private int[] GamePids() => Tracker.FindGameProcesses(Installation()).Select(p => p.Pid).Order().ToArray();

    private int[] PrefixPids(string prefix) => Tracker.FindByPrefix(FileSystem.Shared.FromUnsanitizedFullPath(prefix)).Select(p => p.Pid).Order().ToArray();

    [Fact]
    public void FindByPrefix_MatchesWinePrefixInTheEnvironment()
    {
        AddProcess(100, "wineserver", environ: [$"WINEPREFIX={_prefix}", "HOME=/x"]);
        AddProcess(101, "wineserver", environ: [$"WINEPREFIX={Path.Combine(_root, "other", "pfx")}"]);
        AddProcess(102, "bash", environ: ["HOME=/x"]);

        PrefixPids(_prefix).Should().Equal(100);
    }

    [Fact]
    public void FindByPrefix_MatchesSteamCompatDataPathOfThePrefixParent()
    {
        var compatData = Path.GetDirectoryName(_prefix)!;
        AddProcess(100, "wine64-preloader", environ: [$"STEAM_COMPAT_DATA_PATH={compatData}"]);
        AddProcess(101, "wine64-preloader", environ: [$"STEAM_COMPAT_DATA_PATH={Path.Combine(_root, "steam", "steamapps", "compatdata", "43")}"]);

        PrefixPids(_prefix).Should().Equal(100);
    }

    [Fact]
    public void FindByPrefix_ComparesPathsAfterResolvingSymlinks()
    {
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, Path.Combine(_root, "steam"));
        AddProcess(100, "wineserver", environ: [$"WINEPREFIX={link}/steamapps/compatdata/42/pfx/"]);

        PrefixPids(_prefix).Should().Equal(100);
    }

    [Fact]
    public void FindByPrefix_SkipsProcessesWhoseEnvironmentCannotBeRead()
    {
        AddProcess(100, "wineserver", environ: [$"WINEPREFIX={_prefix}"]);
        AddProcess(101, "wineserver", environ: [$"WINEPREFIX={_prefix}"]);
        var unreadable = Path.Combine(_proc, "101", "environ");
        File.SetUnixFileMode(unreadable, UnixFileMode.None);
        Directory.CreateDirectory(Path.Combine(_proc, "102"));
        File.WriteAllText(Path.Combine(_proc, "102", "comm"), "gone\n");

        try { PrefixPids(_prefix).Should().Equal(100); }
        finally { File.SetUnixFileMode(unreadable, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }

    [Fact]
    public void FindGameProcesses_MatchesAnExecutableInsideTheGameFolder()
    {
        AddProcess(100, "game", exe: Path.Combine(_gameDir, "bin", "game"));
        AddProcess(101, "other", exe: Path.Combine(_root, "elsewhere", "other"));

        Tracker.FindGameProcesses(Installation()).Should().Equal(new TrackedProcess(100, "game"));
    }

    [Fact]
    public void FindGameProcesses_DoesNotMatchASiblingFolderWithTheSamePrefix()
    {
        var sibling = Directory.CreateDirectory(_gameDir + " 2").FullName;
        AddProcess(100, "game", exe: Path.Combine(sibling, "game"));

        GamePids().Should().BeEmpty();
    }

    [Fact]
    public void FindGameProcesses_ResolvesSymlinksInTheGameFolder()
    {
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, _gameDir);
        AddProcess(100, "game", exe: Path.Combine(_gameDir, "game"));

        var game = Substitute.For<IGameData>();
        var linked = FileSystem.Shared.FromUnsanitizedFullPath(link);
        var installation = Installation();
        var viaLink = installation with
        {
            LocatorResult = new GameLocatorResult
            {
                Game = game, Path = linked, Store = GameStore.Steam, StoreIdentifier = "42",
                LocatorIds = ImmutableArray<LocatorId>.Empty, Locator = Substitute.For<IGameLocator>(),
            },
            Locations = GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, linked)),
        };

        Tracker.FindGameProcesses(viaLink).Select(p => p.Pid).Should().Equal(100);
    }

    [Fact]
    public void FindGameProcesses_MatchesTheSteamCompatDataPathInTheEnvironment()
    {
        var compatData = Path.GetDirectoryName(_prefix)!;
        AddProcess(100, "wineserver", environ: ["HOME=/home/u", $"STEAM_COMPAT_DATA_PATH={compatData}"]);
        AddProcess(101, "wineserver", environ: ["STEAM_COMPAT_DATA_PATH=" + Path.Combine(_root, "steam", "steamapps", "compatdata", "43")]);

        GamePids().Should().Equal(100);
    }

    [Fact]
    public void FindGameProcesses_MatchesTheWinePrefixInTheEnvironment()
    {
        AddProcess(100, "wine64-preloader", environ: [$"WINEPREFIX={_prefix}"]);
        AddProcess(101, "wine64-preloader", environ: [$"WINEPREFIX={Path.Combine(_root, "other-prefix")}"]);

        GamePids().Should().Equal(100);
    }

    [Fact]
    public void FindGameProcesses_MatchesAWineProcessGivenAZPathInsideTheGameFolder()
    {
        var windowsPath = "Z:" + Path.Combine(_gameDir, "bin", "game.exe").Replace('/', '\\');
        AddProcess(100, "game.exe", exe: "/opt/wine/bin/wine64-preloader", argv: ["/opt/wine/bin/wine64-preloader", windowsPath]);
        AddProcess(101, "other.exe", exe: "/opt/wine/bin/wine64-preloader", argv: ["/opt/wine/bin/wine64-preloader", @"Z:\opt\other\other.exe"]);

        GamePids().Should().Equal(100);
    }

    [Fact]
    public void FindGameProcesses_AWorkingDirectoryInsideTheGameFolderAloneDoesNotMatch()
    {
        AddProcess(100, "bash", exe: "/usr/bin/bash", cwd: _gameDir);

        GamePids().Should().BeEmpty();
    }

    [Fact]
    public void FindGameProcesses_SkipsAProcessWhoseFilesCannotBeRead()
    {
        // A process that exited or belongs to another user leaves an entry with unreadable files.
        Directory.CreateDirectory(Path.Combine(_proc, "100"));
        File.WriteAllText(Path.Combine(_proc, "self"), "not a process");
        AddProcess(101, "game", exe: Path.Combine(_gameDir, "game"));

        GamePids().Should().Equal(101);
    }

    [Fact]
    public void FindByName_MatchesTheKernelNameIgnoringCase()
    {
        AddProcess(100, "Steam");
        AddProcess(101, "steamwebhelper");

        Tracker.FindByName("steam").Should().Equal(new TrackedProcess(100, "Steam"));
    }

    [Fact]
    public void FindByName_MatchesAWineProcessByTheExecutableItRuns()
    {
        // The kernel cuts the name to 15 characters, so only the command line has the whole name.
        AddProcess(100, "Cyberpunk2077.e", exe: "/opt/wine/bin/wine64-preloader",
            argv: ["/opt/wine/bin/wine64-preloader", @"Z:\games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe"]);

        Tracker.FindByName("Cyberpunk2077.exe").Select(p => p.Pid).Should().Equal(100);
        Tracker.FindByName("Cyberpunk2077").Should().BeEmpty();
    }

    [Fact]
    public void IsSteamRunning_IsTrueOnlyWhileASteamProcessExists()
    {
        AddProcess(100, "steamwebhelper");
        Tracker.IsSteamRunning().Should().BeFalse();

        AddProcess(101, "steam");
        Tracker.IsSteamRunning().Should().BeTrue();
    }

    [Fact]
    public void FindReaperForApp_IgnoresAnotherGamesReaper()
    {
        AddProcess(100, "reaper", argv: ["reaper", "SteamLaunch", "AppId=1091500", "--", "proton"]);
        AddProcess(101, "reaper", argv: ["reaper", "SteamLaunch", "AppId=10915", "--", "proton"]);

        Tracker.FindReaperForApp(10915).Should().Be(new TrackedProcess(101, "reaper"));
        Tracker.FindReaperForApp(1091500).Should().Be(new TrackedProcess(100, "reaper"));
        Tracker.FindReaperForApp(7).Should().BeNull();
    }

    [Fact]
    public void FindReaperForApp_IgnoresANonReaperProcessMentioningTheApp()
    {
        AddProcess(100, "bash", argv: ["bash", "AppId=7"]);

        Tracker.FindReaperForApp(7).Should().BeNull();
    }
}
