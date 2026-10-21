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
