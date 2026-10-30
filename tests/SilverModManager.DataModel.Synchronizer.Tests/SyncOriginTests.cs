using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// Every sync says what started it, so a folder that changed can be traced to a button, a launch or a command.
/// </summary>
public class SyncOriginTests(ITestOutputHelper helper) : AStubbedGameTest<SyncOriginTests>(helper)
{
    private readonly CapturedLogs _logs = new();

    protected override IServiceCollection AddServices(IServiceCollection services) => _logs.AddTo(base.AddServices(services));

    private IEnumerable<CapturedLine> StartLines => _logs.Lines.Where(l => l.Message.StartsWith("Starting sync of loadout") || l.Message.StartsWith("Starting reset of"));

    [Fact]
    public async Task ASyncLogsItsLoadoutItsRevisionAndWhoStartedIt()
    {
        var loadout = await CreateLoadout();

        using (SyncOrigin.Begin(SyncTrigger.ApplyButton)) await SynchronizerService.Synchronize(loadout.LoadoutId);

        var line = StartLines.Last();
        line.Level.Should().Be(LogLevel.Information);
        line.Message.Should().Contain(loadout.Name).And.Contain(loadout.LoadoutId.ToString())
            .And.MatchRegex(@"at revision Tx:[0-9A-F]+, started by the Apply button");
    }

    [Fact]
    public async Task ASyncWithNoRecordedOriginSaysSoAsAWarning()
    {
        var loadout = await CreateLoadout();

        await SynchronizerService.Synchronize(loadout.LoadoutId);

        var line = StartLines.Last();
        line.Level.Should().Be(LogLevel.Warning);
        line.Message.Should().Contain("no recorded origin");
    }

    [Fact]
    public async Task AScopeInsideAnotherKeepsTheOutermostOrigin()
    {
        var loadout = await CreateLoadout();

        using (SyncOrigin.Begin(SyncTrigger.Launch))
        using (SyncOrigin.Begin(SyncTrigger.ApplyButton))
        {
            await SynchronizerService.Synchronize(loadout.LoadoutId);
        }

        StartLines.Last().Message.Should().Contain("started by launching the game");
        SyncOrigin.Current.Should().BeNull("the scope ended with the using");
    }

    [Fact]
    public async Task PuttingTheFolderBackNamesWhatAskedForIt()
    {
        var loadout = await CreateLoadout();
        await SynchronizerService.Synchronize(loadout.LoadoutId);

        await SynchronizerService.CleanGameFolder(loadout.LoadoutId);

        StartLines.Should().Contain(l => l.Message.StartsWith("Starting sync of loadout") && l.Message.Contains("started by putting the game folder back"));
    }

    [Fact]
    public async Task ResettingTheFolderLogsItsOwnStartLine()
    {
        var loadout = await CreateLoadout();
        await SynchronizerService.Synchronize(loadout.LoadoutId);

        using (SyncOrigin.Begin(SyncTrigger.SwitchLoadout)) await LoadoutManager.DeactivateCurrentLoadout(GameInstallation);

        StartLines.Should().Contain(l => l.Message.StartsWith("Starting reset of the game folder") && l.Message.Contains("started by switching loadouts"));
    }

    [Theory]
    [MemberData(nameof(AllTriggers))]
    public void EveryTriggerIsDescribedInPlainWords(SyncTrigger trigger)
    {
        var text = SyncOrigin.Describe(trigger);

        text.Should().NotBeNullOrWhiteSpace();
        text.Should().NotContain(trigger.ToString(), "the log is read by people, not by enum names");
    }

    public static TheoryData<SyncTrigger> AllTriggers() => new(Enum.GetValues<SyncTrigger>());
}
