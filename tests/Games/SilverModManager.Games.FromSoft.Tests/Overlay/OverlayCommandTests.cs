using FluentAssertions;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.Games.FromSoft.Overlay;
using NexusMods.Paths;

namespace NexusMods.Games.FromSoft.Tests.Overlay;

public class OverlayCommandTests
{
    private static AbsolutePath Path(string path) => FileSystem.Shared.FromUnsanitizedFullPath(path);

    private static readonly OverlayLayout Layout = new(Path("/steam/ELDEN RING/Game"), Path("/data/EldenRing/1245620/overlay/7"));

    private static string[] Build() => OverlayCommand.Build(Layout, 1000, 1001,
    [
        "/data/EldenRing/1245620/me3/bin/me3",
        ..Me3Loader.Me3Launch, "/data/EldenRing/1245620/mods/silver.me3",
        ..Me3Loader.GameSelection("eldenring"),
        ..Me3Loader.WindowsBinariesFlag, "/data/EldenRing/1245620/me3/bin/win64",
    ]);

    /// <summary>The group appears as one run of arguments, in that order.</summary>
    private static void ContainsGroup(string[] argv, params string[] group)
    {
        var at = Enumerable.Range(0, argv.Length - group.Length + 1).Cast<int?>().FirstOrDefault(i => argv.Skip(i!.Value).Take(group.Length).SequenceEqual(group));
        at.Should().NotBeNull($"the argv {string.Join(' ', argv)} should hold {string.Join(' ', group)} as one run");
    }

    [Fact]
    public void TheProgramComesFirst()
    {
        Build()[0].Should().Be("bwrap");
    }

    [Fact]
    public void TheUserNamespaceGroupIsThere()
    {
        ContainsGroup(Build(), OverlayCommand.UserNamespace);
    }

    [Fact]
    public void TheIdentityGroupPresentsTheGivenUserAndGroup()
    {
        ContainsGroup(Build(), OverlayCommand.UidFlag, "1000", OverlayCommand.GidFlag, "1001");
    }

    [Fact]
    public void TheRootAndDeviceBindsAndTheProcMountAreThere()
    {
        var argv = Build();

        ContainsGroup(argv, OverlayCommand.RootBind);
        ContainsGroup(argv, OverlayCommand.DeviceBind);
        ContainsGroup(argv, OverlayCommand.ProcMount);
    }

    [Fact]
    public void TheOverlayIsMountedOntoTheLowerFolderItself()
    {
        var argv = Build();

        ContainsGroup(argv, OverlayCommand.OverlaySourceFlag, "/steam/ELDEN RING/Game");
        ContainsGroup(argv, OverlayCommand.OverlayFlag, "/data/EldenRing/1245620/overlay/7/upper", "/data/EldenRing/1245620/overlay/7/work", "/steam/ELDEN RING/Game");
    }

    [Fact]
    public void TheSeparatorEndsBwrapsFlagsAndMe3FollowsIt()
    {
        var argv = Build();
        var separator = Array.IndexOf(argv, OverlayCommand.Separator);

        separator.Should().BeGreaterThan(0);
        argv[separator + 1].Should().Be("/data/EldenRing/1245620/me3/bin/me3");
        argv.Take(separator).Should().NotContain("/data/EldenRing/1245620/me3/bin/me3");
    }

    [Fact]
    public void Me3LaunchesTheGeneratedProfile()
    {
        ContainsGroup(Build(), [..Me3Loader.Me3Launch, "/data/EldenRing/1245620/mods/silver.me3"]);
    }

    [Fact]
    public void TheGameSelectionAndTheWindowsBinariesGroupsAreThereAfterTheSeparator()
    {
        var argv = Build();
        var afterSeparator = argv.Skip(Array.IndexOf(argv, OverlayCommand.Separator) + 1).ToArray();

        ContainsGroup(afterSeparator, Me3Loader.GameSelection("eldenring"));
        ContainsGroup(afterSeparator, [..Me3Loader.WindowsBinariesFlag, "/data/EldenRing/1245620/me3/bin/win64"]);
    }

    [Fact]
    public void NothingUnderTheGameFolderIsRunAndNothingButTheOverlayNamesIt()
    {
        var argv = Build();
        var separator = Array.IndexOf(argv, OverlayCommand.Separator);

        argv.Skip(separator + 1).Should().NotContain(a => a.StartsWith("/steam/ELDEN RING"));
    }

    [Fact]
    public void AnyCommandTailIsPlacedVerbatimAfterTheSeparator()
    {
        var argv = OverlayCommand.Build(Layout, 1000, 1001, ["/x/loader", "run", "--game", "other"]);

        argv.Skip(Array.IndexOf(argv, OverlayCommand.Separator) + 1).Should().Equal("/x/loader", "run", "--game", "other");
    }
}
