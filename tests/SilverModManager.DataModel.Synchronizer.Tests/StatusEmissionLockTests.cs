using System.Collections.Concurrent;
using System.Text;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A subscriber to a loadout's status can do anything with the notification, including putting a file in the file
/// store, which raises a trigger for the very pipeline that is delivering it.
/// </summary>
public class StatusEmissionLockTests(ITestOutputHelper helper) : AStubbedGameTest<StatusEmissionLockTests>(helper)
{
    private Task Backup(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory($"{content}.txt", new MemoryStream(bytes)), FileIdentity.XxHash3(bytes.xxHash3()), Size.FromLong(bytes.Length))]);
    }

    // The emission used to be delivered while the pipeline held its combining lock, so a backup started from the
    // subscriber needed that lock to signal the pipeline and neither could go on.
    [Fact]
    public async Task ASubscriberThatBacksUpAFileFromAStatusNotification_DoesNotStallThePipeline()
    {
        var loadout = await CreateLoadout();
        var statuses = new ConcurrentQueue<LoadoutSynchronizerState>();
        var backupFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var once = 0;

        var stream = await SynchronizerService.StatusForLoadout(loadout.LoadoutId);
        using var watch = stream.Subscribe(state =>
        {
            statuses.Enqueue(state);
            if (statuses.Count < 2 || Interlocked.Exchange(ref once, 1) == 1) return;

            // Waits on this thread, as a handler that does its work inline would.
            backupFinished.TrySetResult(Task.Run(() => Backup("from the subscriber")).Wait(TimeSpan.FromSeconds(15)));
        });

        while (statuses.IsEmpty) await Task.Delay(20, TestContext.Current.CancellationToken);
        await Backup("the trigger");

        (await backupFinished.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken))
            .Should().BeTrue("the backup raises the archive trigger, which has to get through while the subscriber is being told");
    }
}
