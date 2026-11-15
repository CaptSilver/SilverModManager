using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.Library.Tests.Lock;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using Xunit;

namespace SilverModManager.Library.Tests.ModSources;

/// <summary>
/// The shared update flow every mod source runs its steps through. The steps are small in-test subclasses; the
/// library service is a substitute, so what is asserted is what the batch asked of it.
/// </summary>
public class ModUpdateBatchTests(IConnection connection)
{
    private async Task<LibraryItem.ReadOnly> NewItem()
        => (await new LockFixtures(connection).AddLibraryFile("batch-" + Guid.NewGuid() + ".zip", "batch " + Guid.NewGuid(), null)).AsLibraryItem();

    private static ILibraryService Library()
    {
        var library = Substitute.For<ILibraryService>();
        library.ReplaceLinkedItemsInAllLoadouts(default, default, default).ReturnsForAnyArgs(ValueTask.FromResult(LibraryItemReplacementResult.Success));
        return library;
    }

    private static ModUpdateBatch Batch(ILibraryService? library = null, bool replace = true, CancellationToken token = default)
        => new(new ModUpdateRequest([], replace, MayOpenBrowser: true), library ?? Library(), token);

    private sealed record TestUpdate(string Label) : ModUpdate([], "1.0", "2.0", null);

    // Runs the given body as the step.
    private sealed class DelegateStep(string label, Func<ModUpdateBatch, Task> body) : UpdateStep(new TestUpdate(label), label)
    {
        public override Task Apply(ModUpdateBatch batch) => body(batch);
    }

    private sealed class TestDownloadStep(
        IReadOnlyList<LibraryItem.ReadOnly> current,
        Func<ModUpdateBatch, LibraryItem.ReadOnly?> download) : DownloadThenReplaceStep(new TestUpdate("download"), "download")
    {
        protected override IReadOnlyList<LibraryItem.ReadOnly> LoadCurrent() => current;

        protected override Task<LibraryItem.ReadOnly?> Download(IReadOnlyList<LibraryItem.ReadOnly> loaded, ModUpdateBatch batch)
            => Task.FromResult(download(batch));
    }

    [Fact]
    public async Task Run_FailingStep_RecordsItsNarrowedUpdateAndName_AndLaterStepsStillRun()
    {
        var later = await NewItem();
        var failure = new InvalidOperationException("offline");
        var failing = new DelegateStep("first", _ => Task.FromException(failure));
        var next = new DelegateStep("second", batch =>
        {
            batch.Downloaded(later);
            return Task.CompletedTask;
        });

        var outcome = await Batch().Run([failing, next]);

        var failed = outcome.DownloadFailures.Should().ContainSingle().Subject;
        failed.Update.Should().BeSameAs(failing.Update);
        failed.Name.Should().Be("first");
        failed.Error.Should().BeSameAs(failure);
        outcome.Downloaded.Select(item => item.Id).Should().Equal(later.Id);
    }

    [Fact]
    public async Task Run_UserCancelsDuringAStep_ReportsNoFailure_AndStartsNoFurtherStep()
    {
        using var cancel = new CancellationTokenSource();
        var secondStarted = false;
        var first = new DelegateStep("first", async _ =>
        {
            await cancel.CancelAsync();
            cancel.Token.ThrowIfCancellationRequested();
        });
        var second = new DelegateStep("second", _ =>
        {
            secondStarted = true;
            return Task.CompletedTask;
        });

        var outcome = await Batch(token: cancel.Token).Run([first, second]);

        outcome.DownloadFailures.Should().BeEmpty();
        secondStarted.Should().BeFalse();
    }

    [Fact]
    public async Task Run_CancellationNotFromTheToken_IsAFailure()
    {
        var timeout = new TaskCanceledException("the request timed out");

        var outcome = await Batch().Run([new DelegateStep("slow", _ => Task.FromException(timeout))]);

        outcome.DownloadFailures.Should().ContainSingle().Which.Error.Should().BeSameAs(timeout);
    }

    [Fact]
    public async Task Run_ParallelismOne_StartsStepsInOrder()
    {
        var started = new List<string>();
        var steps = Enumerable.Range(0, 4).Select(i => new DelegateStep("step" + i, async _ =>
        {
            lock (started) started.Add("step" + i);
            await Task.Yield();
        })).ToArray();

        await Batch().Run(steps, parallelism: 1);

        started.Should().Equal("step0", "step1", "step2", "step3");
    }

    [Fact]
    public async Task Run_ParallelismTwo_NeverHasMoreThanTwoStepsRunning()
    {
        var running = 0;
        var peak = 0;
        var gates = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource()).ToArray();
        var reachedTwo = new TaskCompletionSource();
        var steps = gates.Select((gate, i) => new DelegateStep("step" + i, async _ =>
        {
            var now = Interlocked.Increment(ref running);
            int seen;
            while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
            if (now == 2) reachedTwo.TrySetResult();
            await gate.Task;
            Interlocked.Decrement(ref running);
        })).ToArray();

        var run = Batch().Run(steps, parallelism: 2).AsTask();
        // Bounded so a batch that ran the steps one at a time fails here instead of hanging the run.
        await reachedTwo.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // A batch that ignored the limit would already have started all four steps inline by now.
        foreach (var gate in gates) gate.SetResult();
        await run;

        peak.Should().Be(2);
    }

    [Fact]
    public async Task Downloaded_TheSameItemTwice_IsListedOnce()
    {
        var item = await NewItem();
        var batch = Batch();

        batch.Downloaded(item);
        batch.Downloaded(item);
        var outcome = await batch.Run([]);

        outcome.Downloaded.Select(downloaded => downloaded.Id).Should().Equal(item.Id);
    }

    [Fact]
    public async Task DownloadThenReplace_NoCurrentItems_FailsAsNoLongerInTheLibrary()
    {
        var downloads = 0;
        var step = new TestDownloadStep([], _ =>
        {
            downloads++;
            return null;
        });

        var outcome = await Batch().Run([step]);

        outcome.DownloadFailures.Should().ContainSingle().Which.Error.Message.Should().Contain("download 1.0 is no longer in the library");
        downloads.Should().Be(0);
    }

    [Fact]
    public async Task DownloadThenReplace_ReplaceFalse_DownloadsAndReplacesNothing()
    {
        var old = await NewItem();
        var added = await NewItem();
        var library = Library();

        var outcome = await Batch(library, replace: false).Run([new TestDownloadStep([old], _ => added)]);

        outcome.Downloaded.Select(item => item.Id).Should().Equal(added.Id);
        outcome.Replacements.Should().BeEmpty();
        await library.DidNotReceiveWithAnyArgs().ReplaceLinkedItemsInAllLoadouts(default, default, default);
    }

    [Fact]
    public async Task DownloadThenReplace_NullDownload_RecordsNothing()
    {
        var old = await NewItem();
        var library = Library();

        var outcome = await Batch(library).Run([new TestDownloadStep([old], _ => null)]);

        outcome.Downloaded.Should().BeEmpty();
        outcome.Replacements.Should().BeEmpty();
        outcome.DownloadFailures.Should().BeEmpty();
        await library.DidNotReceiveWithAnyArgs().ReplaceLinkedItemsInAllLoadouts(default, default, default);
    }

    [Fact]
    public async Task DownloadThenReplace_Replace_SwapsEveryCurrentItemWithTheModUpdateOptions()
    {
        var first = await NewItem();
        var second = await NewItem();
        var added = await NewItem();
        var library = Library();

        var outcome = await Batch(library).Run([new TestDownloadStep([first, second], _ => added)]);

        foreach (var old in new[] { first, second })
        {
            await library.Received(1).ReplaceLinkedItemsInAllLoadouts(
                Arg.Is<LibraryItem.ReadOnly>(item => item.Id == old.Id),
                Arg.Is<LibraryItem.ReadOnly>(item => item.Id == added.Id),
                Arg.Is<ReplaceLibraryItemOptions>(options => options == ReplaceLibraryItemOptions.ModUpdate));
        }
        outcome.Replacements.Select(replacement => replacement.Old.Id).Should().Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task Replace_WithoutALibraryService_Throws()
    {
        var old = await NewItem();
        var batch = new ModUpdateBatch(new ModUpdateRequest([], Replace: true, MayOpenBrowser: true), library: null, CancellationToken.None);

        var replace = async () => await batch.Replace(old, old);

        await replace.Should().ThrowAsync<InvalidOperationException>();
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services.AddLibraryModels();
        }
    }
}
