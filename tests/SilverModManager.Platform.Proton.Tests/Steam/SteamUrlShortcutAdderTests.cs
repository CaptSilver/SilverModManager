using FluentAssertions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Platform.Proton.Steam.Shortcuts;
using SilverModManager.Sdk;

namespace SilverModManager.Platform.Proton.Tests.Steam;

public class SteamUrlShortcutAdderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("silvermod-urladd-").FullName;
    private readonly ISteamUriLauncher _launcher = Substitute.For<ISteamUriLauncher>();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AbsolutePath P(string path) => FileSystem.Shared.FromUnsanitizedFullPath(System.IO.Path.Combine(_root, path));
    private string Marker => System.IO.Path.Combine(_root, "addnonsteamgamefile");

    private SteamShortcutRequest Request(string shim = "bin/silvermod")
        => new(new SteamAccount(42, "alice", "Alice"), P("steam"), P(shim), P("icons/x.png"));

    private SteamUrlShortcutAdder Adder(params uint[][] snapshots)
    {
        var calls = 0;
        return new SteamUrlShortcutAdder(_launcher, _ => snapshots[Math.Min(calls++, snapshots.Length - 1)].ToHashSet(), P("addnonsteamgamefile"));
    }

    [Fact]
    public void EncodesThePathWithNoSafeCharacters()
    {
        SteamUrlShortcutAdder.Encode("/home/u/a b/x").Should().Be("%2Fhome%2Fu%2Fa%20b%2Fx");
        SteamUrlShortcutAdder.Encode("a-b_c.d~e").Should().Be("a-b_c.d~e");
        SteamUrlShortcutAdder.Encode("é").Should().Be("%C3%A9");
    }

    [Fact]
    public async Task TouchesTheMarkerThenHandsTheUrlToTheLauncher()
    {
        var markerExistedWhenOpened = false;
        _launcher.When(l => l.Open(Arg.Any<Uri>())).Do(_ => markerExistedWhenOpened = File.Exists(Marker));

        await Adder([1u], [1u]).AddAsync(Request("my dir/silvermod"), CancellationToken.None);

        markerExistedWhenOpened.Should().BeTrue();
        _launcher.Received(1).Open(Arg.Is<Uri>(u => u.AbsoluteUri == "steam://addnonsteamgame/" + SteamUrlShortcutAdder.Encode(P("my dir/silvermod").ToString())));
        ((Uri)_launcher.ReceivedCalls().Single().GetArguments()[0]!).AbsoluteUri.Should().Contain("%2F").And.Contain("%20");
    }

    [Fact]
    public async Task AMarkerThatIsALinkIsRefusedAndWhatItPointsAtIsLeftAlone()
    {
        // The marker sits in /tmp, where anyone can leave a link under its name.
        var victim = System.IO.Path.Combine(_root, "victim.txt");
        await File.WriteAllTextAsync(victim, "keep");
        File.CreateSymbolicLink(Marker, victim);

        var act = () => Adder([1u]).AddAsync(Request(), CancellationToken.None);

        await act.Should().ThrowAsync<SteamShortcutRefusedException>().WithMessage("*link*");
        (await File.ReadAllTextAsync(victim)).Should().Be("keep");
        _launcher.DidNotReceiveWithAnyArgs().Open(default!);
    }

    [Fact]
    public async Task AMarkerLeftByAnEarlierAddIsMadeAgain()
    {
        await File.WriteAllTextAsync(Marker, "left over");

        await Adder([1u], [1u]).AddAsync(Request(), CancellationToken.None);

        new FileInfo(Marker).Length.Should().Be(0, "the marker is made afresh, not written through");
        _launcher.ReceivedWithAnyArgs(1).Open(default!);
    }

    [Fact]
    public async Task TheOneNewAppIdIsRecordedWhenSteamFlushesAtOnce()
    {
        var write = await Adder([1u], [1u, 2147483650u]).AddAsync(Request(), CancellationToken.None);

        write.AppId.Should().Be(2147483650u);
        write.Confirmed.Should().BeTrue();
        write.FilesWritten.Should().BeEmpty();
    }

    [Fact]
    public async Task NoNewAppIdLeavesTheWriteUnconfirmed()
    {
        var write = await Adder([1u], [1u]).AddAsync(Request(), CancellationToken.None);

        write.AppId.Should().BeNull();
        write.Confirmed.Should().BeFalse();
    }

    [Fact]
    public async Task MoreThanOneNewAppIdIsNotGuessedAt()
    {
        var write = await Adder([1u], [1u, 2147483650u, 2147483651u]).AddAsync(Request(), CancellationToken.None);

        write.AppId.Should().BeNull();
        write.Confirmed.Should().BeFalse();
    }

    [Fact]
    public async Task TheAppIdsAreReadFromTheAccountsShortcutsFile()
    {
        var paths = new List<string>();
        var adder = new SteamUrlShortcutAdder(_launcher, path => { paths.Add(path); return new HashSet<uint>(); }, P("addnonsteamgamefile"));

        await adder.AddAsync(Request(), CancellationToken.None);

        paths.Should().AllBe(P("steam/userdata/42/config/shortcuts.vdf").ToString());
        adder.Name.Should().Be("steam-url");
    }
}
