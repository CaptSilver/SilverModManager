using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.App.UI;
using NexusMods.App.UI.Pages.SteamLaunchOption;
using NexusMods.Abstractions.Games;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Platform.Proton.Steam;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NSubstitute;
using Loadout = NexusMods.Sdk.Loadouts.Loadout;

namespace NexusMods.UI.Tests;

public class SteamLaunchOptionViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    private const string Line = "'/data/silvermod/bin/silvermod' run 'Loadout A' -- %command%";

    private sealed class Fixture
    {
        public IProcessTracker Tracker { get; } = Substitute.For<IProcessTracker>();
        public ISteamLaunchOptionWriter Writer { get; } = Substitute.For<ISteamLaunchOptionWriter>();
        public IAvaloniaInterop Interop { get; } = Substitute.For<IAvaloniaInterop>();
        public Loadout.ReadOnly Seeded { get; }
        public SteamLaunchOptionViewModel Vm { get; }

        public Fixture(Loadout.ReadOnly loadout, GameInstallation installation, bool steamRunning, string? shimWarning = null)
        {
            Seeded = loadout;
            Tracker.IsSteamRunning().Returns(steamRunning);
            Writer.ComposeLine(Arg.Is<Loadout.ReadOnly>(l => l.LoadoutId == loadout.LoadoutId)).Returns(Line);
            Writer.MissingShimWarning.Returns(shimWarning);
            Vm = new SteamLaunchOptionViewModel(loadout, installation, Tracker, Writer, Interop);
        }
    }

    private async Task<Fixture> NewFixture(bool steamRunning, string? shimWarning = null)
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Loadout A");
        return new Fixture(loadout, Installation, steamRunning, shimWarning);
    }

    private GameInstallation Installation => Provider.GetRequiredService<IGameRegistry>().LocateGameInstallations().First();

    [Fact]
    public async Task Test_ShowsTheComposedLine()
    {
        var f = await NewFixture(steamRunning: false);
        f.Vm.Line.Should().Be(Line);
    }

    [Fact]
    public async Task Test_MissingLauncherScriptIsShownAsAWarning()
    {
        var f = await NewFixture(steamRunning: false, shimWarning: "no script yet");
        f.Vm.Warning.Should().Be("no script yet");
        (await NewFixture(steamRunning: false)).Vm.Warning.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_SteamRunning_CopyWorksAndWriteExplains()
    {
        var f = await NewFixture(steamRunning: true);

        f.Vm.CanWrite.Should().BeFalse();
        f.Vm.WriteNote.Should().Contain("Close Steam");

        await f.Vm.CopyAsync();
        await f.Interop.Received(1).SetClipboardTextAsync(Line);

        await f.Vm.WriteAsync();
        f.Writer.DidNotReceiveWithAnyArgs().Write(default!, default!);
        f.Vm.Message.Should().Contain("Close Steam");
    }

    [Fact]
    public async Task Test_SteamClosed_WriteCallsTheWriterAndReportsTheOutcome()
    {
        var f = await NewFixture(steamRunning: false);
        var backup = FileSystem.Shared.FromUnsanitizedFullPath("/tmp/localconfig.vdf.silvermod-bak");
        f.Writer.Write(f.Vm.Installation, Arg.Is<Loadout.ReadOnly>(l => l.LoadoutId == f.Seeded.LoadoutId)).Returns(new LocalConfigVdfWriter.Result(Line, null, backup));

        f.Vm.CanWrite.Should().BeTrue();
        f.Vm.WriteNote.Should().BeEmpty();

        await f.Vm.WriteAsync();

        f.Writer.Received(1).Write(f.Vm.Installation, Arg.Is<Loadout.ReadOnly>(l => l.LoadoutId == f.Seeded.LoadoutId));
        f.Vm.Message.Should().Contain(Line).And.Contain("localconfig.vdf.silvermod-bak");
        await f.Interop.DidNotReceiveWithAnyArgs().SetClipboardTextAsync(default!);
    }

    [Fact]
    public async Task Test_WriteRefusalTextComesFromTheWriter()
    {
        var f = await NewFixture(steamRunning: false);
        f.Writer.Write(f.Vm.Installation, Arg.Is<Loadout.ReadOnly>(l => l.LoadoutId == f.Seeded.LoadoutId)).Returns(new LocalConfigVdfWriter.Result(null, "an unquoted ';' before %command%", null));

        await f.Vm.WriteAsync();

        f.Vm.Message.Should().Be("an unquoted ';' before %command%");
    }

    private const string FluorineLine = "'/opt/fluorine-manager/fluorine-manager' --game 'Cyberpunk' # %command%";

    private static LocalConfigVdfWriter.Replacement FluorineReplacement(string oldLine = FluorineLine) => new("Fluorine", oldLine, Line);

    private static LocalConfigVdfWriter.Result AnotherManagersRefusal(LocalConfigVdfWriter.Replacement replacement) =>
        new(null, "Steam's launch options for this game belong to Fluorine.", null, replacement);

    [Fact]
    public async Task Test_ARefusedWriteForAnotherManagerOffersReplace()
    {
        var f = await NewFixture(steamRunning: false);
        f.Writer.Write(f.Vm.Installation, Arg.Is<Loadout.ReadOnly>(l => l.LoadoutId == f.Seeded.LoadoutId)).Returns(AnotherManagersRefusal(FluorineReplacement()));

        await f.Vm.WriteAsync();

        f.Vm.CanReplace.Should().BeTrue();
        f.Vm.OtherManagerName.Should().Be("Fluorine");
        f.Vm.OldLine.Should().Be(FluorineLine);
        f.Vm.NewLine.Should().Be(Line);
        f.Vm.Message.Should().Be("Steam's launch options for this game belong to Fluorine.");
    }

    [Fact]
    public async Task Test_AnUnrecognisedManagerIsNamedGenerically()
    {
        var f = await NewFixture(steamRunning: false);
        f.Writer.Write(f.Vm.Installation, Arg.Is<Loadout.ReadOnly>(l => l.LoadoutId == f.Seeded.LoadoutId))
            .Returns(AnotherManagersRefusal(new LocalConfigVdfWriter.Replacement(null, FluorineLine, Line)));

        await f.Vm.WriteAsync();

        f.Vm.OtherManagerName.Should().Be("another launcher");
    }

    [Fact]
    public async Task Test_ReplaceSendsTheShownOldLineToTheWriter()
    {
        var f = await NewFixture(steamRunning: false);
        var backup = FileSystem.Shared.FromUnsanitizedFullPath("/tmp/localconfig.vdf.silvermod-bak");
        var record = FileSystem.Shared.FromUnsanitizedFullPath("/tmp/replaced-launch-options.txt");
        f.Writer.Write(f.Vm.Installation, Arg.Is<Loadout.ReadOnly>(l => l.LoadoutId == f.Seeded.LoadoutId)).Returns(AnotherManagersRefusal(FluorineReplacement()));
        f.Writer.Replace(f.Vm.Installation, Arg.Is<Loadout.ReadOnly>(l => l.LoadoutId == f.Seeded.LoadoutId), FluorineLine)
            .Returns(new LocalConfigVdfWriter.Result(Line, null, backup, FluorineReplacement(), record));

        await f.Vm.WriteAsync();
        await f.Vm.ReplaceAsync();

        f.Writer.Received(1).Replace(f.Vm.Installation, Arg.Is<Loadout.ReadOnly>(l => l.LoadoutId == f.Seeded.LoadoutId), FluorineLine);
        f.Vm.Message.Should().Contain("Replaced the launch options of").And.Contain("replaced-launch-options.txt").And.Contain("localconfig.vdf.silvermod-bak");
        f.Vm.CanReplace.Should().BeFalse();
    }

    [Fact]
    public async Task Test_ReplaceIsNotOfferedForAnOrdinaryRefusal()
    {
        var f = await NewFixture(steamRunning: false);
        f.Writer.Write(f.Vm.Installation, Arg.Is<Loadout.ReadOnly>(l => l.LoadoutId == f.Seeded.LoadoutId)).Returns(new LocalConfigVdfWriter.Result(null, "an unquoted ';' before %command%", null));

        await f.Vm.WriteAsync();

        f.Vm.CanReplace.Should().BeFalse();
        f.Vm.OldLine.Should().BeEmpty();
        f.Vm.NewLine.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_ReplaceChecksSteamAgainWhenPressed()
    {
        var f = await NewFixture(steamRunning: false);
        f.Writer.Write(f.Vm.Installation, Arg.Is<Loadout.ReadOnly>(l => l.LoadoutId == f.Seeded.LoadoutId)).Returns(AnotherManagersRefusal(FluorineReplacement()));
        await f.Vm.WriteAsync();
        f.Vm.CanReplace.Should().BeTrue();

        f.Tracker.IsSteamRunning().Returns(true);
        await f.Vm.ReplaceAsync();

        f.Writer.DidNotReceiveWithAnyArgs().Replace(default!, default!, default!);
        f.Vm.Message.Should().Contain("Close Steam");
        f.Vm.CanReplace.Should().BeFalse();
    }

    [Fact]
    public async Task Test_ReplaceFollowsSteamStateWhileAReplacementIsPending()
    {
        var f = await NewFixture(steamRunning: false);
        f.Writer.Write(f.Vm.Installation, Arg.Is<Loadout.ReadOnly>(l => l.LoadoutId == f.Seeded.LoadoutId)).Returns(AnotherManagersRefusal(FluorineReplacement()));
        await f.Vm.WriteAsync();

        f.Tracker.IsSteamRunning().Returns(true);
        f.Vm.RefreshSteamState();
        f.Vm.CanReplace.Should().BeFalse();

        f.Tracker.IsSteamRunning().Returns(false);
        f.Vm.RefreshSteamState();
        f.Vm.CanReplace.Should().BeTrue();
    }

    [Fact]
    public async Task Test_AChangedLineRefreshesWhatTheNextPressConfirms()
    {
        var f = await NewFixture(steamRunning: false);
        const string changed = "'/opt/fluorine-manager/fluorine-manager' --game 'Other' # %command%";
        f.Writer.Write(f.Vm.Installation, Arg.Is<Loadout.ReadOnly>(l => l.LoadoutId == f.Seeded.LoadoutId)).Returns(AnotherManagersRefusal(FluorineReplacement()));
        f.Writer.Replace(f.Vm.Installation, Arg.Is<Loadout.ReadOnly>(l => l.LoadoutId == f.Seeded.LoadoutId), FluorineLine)
            .Returns(new LocalConfigVdfWriter.Result(null, "The launch options changed since they were shown.", null, FluorineReplacement(changed)));

        await f.Vm.WriteAsync();
        await f.Vm.ReplaceAsync();

        f.Vm.Message.Should().Contain("changed since");
        f.Vm.OldLine.Should().Be(changed);
        f.Vm.CanReplace.Should().BeTrue();
    }

    [Fact]
    public async Task Test_ASuccessfulWriteClearsAPendingReplace()
    {
        var f = await NewFixture(steamRunning: false);
        var backup = FileSystem.Shared.FromUnsanitizedFullPath("/tmp/localconfig.vdf.silvermod-bak");
        f.Writer.Write(f.Vm.Installation, Arg.Is<Loadout.ReadOnly>(l => l.LoadoutId == f.Seeded.LoadoutId)).Returns(
            AnotherManagersRefusal(FluorineReplacement()),
            new LocalConfigVdfWriter.Result(Line, null, backup));

        await f.Vm.WriteAsync();
        f.Vm.CanReplace.Should().BeTrue();
        await f.Vm.WriteAsync();

        f.Vm.CanReplace.Should().BeFalse();
        f.Vm.OldLine.Should().BeEmpty();
        f.Vm.NewLine.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_WriteChecksSteamAgainWhenPressed()
    {
        var f = await NewFixture(steamRunning: false);
        f.Vm.CanWrite.Should().BeTrue();

        // Steam was started after the dialog opened and nothing polled yet.
        f.Tracker.IsSteamRunning().Returns(true);
        await f.Vm.WriteAsync();

        f.Writer.DidNotReceiveWithAnyArgs().Write(default!, default!);
        f.Vm.IsSteamRunning.Should().BeTrue();
        f.Vm.CanWrite.Should().BeFalse();
        f.Vm.Message.Should().Contain("Close Steam");
    }

    [Fact]
    public async Task Test_RefreshFollowsSteamClosing()
    {
        var f = await NewFixture(steamRunning: true);
        f.Vm.CanWrite.Should().BeFalse();

        f.Tracker.IsSteamRunning().Returns(false);
        f.Vm.RefreshSteamState();
        f.Vm.CanWrite.Should().BeTrue();
        f.Vm.WriteNote.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_CopySaysItChangesNothing()
    {
        var f = await NewFixture(steamRunning: false);
        f.Vm.CopyNote.Should().Contain("changes nothing");

        await f.Vm.CopyAsync();

        f.Vm.Message.Should().Contain("Copied");
        f.Writer.DidNotReceiveWithAnyArgs().Write(default!, default!);
    }

    [Fact]
    public async Task Test_AppRegistersTheWriterTheDialogNeeds()
    {
        var writer = Provider.GetRequiredService<ISteamLaunchOptionWriter>();
        writer.ComposeLine(await SeedLoadoutRow(GameStore.Steam, "Loadout A")).Should().EndWith("run 'Loadout A' -- %command%");
    }
}
