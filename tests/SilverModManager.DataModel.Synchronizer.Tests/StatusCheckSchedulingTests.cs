using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// The changes-found status walks every file of the loadout, so it must not run once per revision of a burst, and not
/// at all while an install is still writing into the loadout: the answer would be stale at the next commit and the
/// walks compete with the install.
/// </summary>
public class StatusCheckSchedulingTests(ITestOutputHelper helper) : AStubbedGameTest<StatusCheckSchedulingTests>(helper)
{
    // The status pipeline waits on this clock, so a test decides when a burst is over instead of racing a timer.
    private readonly FakeTimeProvider _time = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services).AddSingleton<TimeProvider>(_time);

    private FakeLoadoutInstallActivity Installs => ServiceProvider.GetRequiredService<FakeLoadoutInstallActivity>();

    private readonly List<LoadoutId> _checked = [];

    private IDisposable CountChecks()
        => ServiceProvider.GetRequiredService<IJobMonitor>().GetObservableChangeSet<ProcessLoadoutChangesJob>()
            .Subscribe(changes =>
            {
                foreach (var change in changes)
                {
                    if (change.Reason != DynamicData.ChangeReason.Add || change.Current.Definition is not ProcessLoadoutChangesJob job) continue;
                    lock (_checked) _checked.Add(job.LoadoutId);
                }
            });

    private int ChecksOf(LoadoutId id)
    {
        lock (_checked) return _checked.Count(c => c == id);
    }

    private async Task AddMod(LoadoutId loadout, string name)
    {
        using var tx = Connection.BeginTransaction();
        await AddModAsync(tx, [(RelativePath)$"mods/{name}/file.txt"], loadout, name);
        await tx.Commit();
    }

    /// <summary>
    /// Lets the triggers of what was just committed reach the pipeline, ends the quiet period on the fake clock, and
    /// gives a check that this starts time to run. Real waits only cover work that has no clock to move.
    /// </summary>
    private async Task LetTheQuietPeriodPass()
    {
        await Task.Delay(300, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(700, TestContext.Current.CancellationToken);
    }

    private sealed record Watched(IDisposable Subscription, List<LoadoutSynchronizerState> States) : IDisposable
    {
        public int Count { get { lock (States) return States.Count; } }
        public void Dispose() => Subscription.Dispose();
    }

    // Subscribes and lets the first look finish, so later counts start from a quiet pipeline.
    private async Task<Watched> Watch(LoadoutId loadout)
    {
        var states = new List<LoadoutSynchronizerState>();
        var stream = await SynchronizerService.StatusForLoadout(loadout);
        var watch = stream.Subscribe(s => { lock (states) states.Add(s); });
        await LetTheQuietPeriodPass();
        return new Watched(watch, states);
    }

    [Fact]
    public async Task AnInstallThatCommitsManyRevisions_CausesNoCheckWhileItRuns_AndOneWhenItEnds()
    {
        var loadout = await CreateLoadout();
        using var counting = CountChecks();
        using var _ = await Watch(loadout.LoadoutId);
        var before = ChecksOf(loadout.LoadoutId);

        Installs.SetInstalling(loadout.LoadoutId, true);
        for (var i = 0; i < 8; i++)
        {
            await AddMod(loadout.LoadoutId, $"installed{i}");
            await LetTheQuietPeriodPass();
        }
        ChecksOf(loadout.LoadoutId).Should().Be(before, "nothing is checked while the install is writing");

        Installs.SetInstalling(loadout.LoadoutId, false);
        await LetTheQuietPeriodPass();
        ChecksOf(loadout.LoadoutId).Should().Be(before + 1, "one check covers everything the install wrote");
    }

    [Fact]
    public async Task AnInstallThatEnds_WithACommitRightAfter_ChecksOnceAfterIt()
    {
        var loadout = await CreateLoadout();
        using var counting = CountChecks();
        using var _ = await Watch(loadout.LoadoutId);
        var before = ChecksOf(loadout.LoadoutId);

        Installs.SetInstalling(loadout.LoadoutId, true);
        await AddMod(loadout.LoadoutId, "first");
        await AddMod(loadout.LoadoutId, "last");
        Installs.SetInstalling(loadout.LoadoutId, false);
        await AddMod(loadout.LoadoutId, "trailing");
        await LetTheQuietPeriodPass();

        ChecksOf(loadout.LoadoutId).Should().Be(before + 1);
    }

    [Fact]
    public async Task ABurstOfRevisions_CausesOneCheck()
    {
        var loadout = await CreateLoadout();
        using var counting = CountChecks();
        using var _ = await Watch(loadout.LoadoutId);
        var before = ChecksOf(loadout.LoadoutId);

        // The clock does not move between commits, so however slow the machine is, this is one burst.
        for (var i = 0; i < 10; i++) await AddMod(loadout.LoadoutId, $"burst{i}");
        ChecksOf(loadout.LoadoutId).Should().Be(before, "the burst has not settled yet");
        await LetTheQuietPeriodPass();

        ChecksOf(loadout.LoadoutId).Should().Be(before + 1);
    }

    [Fact]
    public async Task ASingleEditOutsideAnInstall_IsCheckedOnceTheQuietPeriodPasses()
    {
        var loadout = await CreateLoadout();
        using var counting = CountChecks();
        using var watched = await Watch(loadout.LoadoutId);
        var before = ChecksOf(loadout.LoadoutId);
        var statesBefore = watched.Count;

        await AddMod(loadout.LoadoutId, "edit");
        await LetTheQuietPeriodPass();

        ChecksOf(loadout.LoadoutId).Should().Be(before + 1, "the file backup the edit starts is part of the same burst");
        watched.Count.Should().BeGreaterThan(statesBefore, "subscribers hear the new status");
    }

    [Fact]
    public async Task AStatusAskedForMidInstall_StillGetsAFirstValue()
    {
        var loadout = await CreateLoadout();
        Installs.SetInstalling(loadout.LoadoutId, true);

        using var watched = await Watch(loadout.LoadoutId);

        watched.Count.Should().BeGreaterThan(0, "the Apply button, the badge and the diff page wait for a first status");
    }

    [Fact]
    public async Task AnUpdateThatStoppedPartWay_DoesNotFreezeTheStatus()
    {
        var loadout = await CreateLoadout();
        using var counting = CountChecks();
        Installs.SetStalled(loadout.LoadoutId);
        using var _ = await Watch(loadout.LoadoutId);
        var before = ChecksOf(loadout.LoadoutId);

        await AddMod(loadout.LoadoutId, "edit");
        await LetTheQuietPeriodPass();

        ChecksOf(loadout.LoadoutId).Should().Be(before + 1, "nothing is writing into the loadout, so an edit is still compared");
    }

    [Fact]
    public async Task AnotherLoadout_IsStillChecked_WhileAnInstallWritesIntoThisOne()
    {
        // Only the loadout applied to the game is compared with the disk, which is the first one; the other answers
        // from its recorded inputs, so what shows it was looked at is a new status reaching its subscribers.
        var installing = await CreateLoadout();
        var other = await CreateLoadout();
        using var watchedInstalling = await Watch(installing.LoadoutId);
        using var watchedOther = await Watch(other.LoadoutId);
        var otherBefore = watchedOther.Count;
        var installingBefore = watchedInstalling.Count;

        Installs.SetInstalling(installing.LoadoutId, true);
        await AddMod(installing.LoadoutId, "installed");
        await AddMod(other.LoadoutId, "edited");
        await LetTheQuietPeriodPass();

        watchedOther.Count.Should().BeGreaterThan(otherBefore, "the other loadout is not part of the install");
        watchedInstalling.Count.Should().Be(installingBefore, "the loadout being installed into is not looked at until the install ends");
    }
}
