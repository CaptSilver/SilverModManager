using System.Collections.Immutable;
using FluentAssertions;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.App.UI.Resources;
using SilverModManager.Collections;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.UI.Tests.Pages;

public class ReapplyCollectionOrderActionHandlerTests(IServiceProvider provider) : AUiTest(provider)
{
    private ReapplyCollectionOrderActionHandler Handler => new(Provider, Substitute.For<IDiagnosticManager>());

    private static readonly LoadoutId Loadout = LoadoutId.From(EntityId.From(0xBEEF));

    [Fact]
    public void Test_TheButtonUsesTheLocalizedLabel()
    {
        var action = DiagnosticActions.ReapplyCollectionOrder(42);

        Handler.LabelFor(action).Should().Be(Language.DiagnosticAction_ReapplyCollectionOrder).And.NotBeNullOrWhiteSpace();
        Handler.ActionId.Should().Be(action.Id);
    }

    [Fact]
    public async Task Test_ACollectionThatIsGone_IsARefusalTheUserCanRead()
    {
        var act = () => Handler.ExecuteAsync(Loadout, DiagnosticActions.ReapplyCollectionOrder(0xDEAD).Arguments);

        (await act.Should().ThrowAsync<CollectionOrderReapplyRefusedException>()).Which.Message.Should().Contain("no longer installed");
    }

    [Fact]
    public async Task Test_AnActionWithoutACollection_IsRejected()
    {
        var act = () => Handler.ExecuteAsync(Loadout, ImmutableArray<string>.Empty);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private sealed class ThreadRecordingProvider(IServiceProvider inner) : IServiceProvider
    {
        public List<int> Threads { get; } = [];

        public object? GetService(Type serviceType)
        {
            lock (Threads) Threads.Add(Environment.CurrentManagedThreadId);
            return inner.GetService(serviceType);
        }
    }

    // Stands in for the UI thread: a thread of its own, outside the pool, that stays blocked until the command is
    // done. A pool thread that called Task.Run could pick its own work item back up once it yielded, and a thread that
    // had ended could hand its managed id to the pool, so neither can stand in for the thread the read must leave.
    private static (int CallerThread, Task Outcome) RunFromADedicatedThread(Func<Task> command)
    {
        var started = new TaskCompletionSource<int>();
        var outcome = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            started.SetResult(Environment.CurrentManagedThreadId);
            try
            {
                command().Wait();
                outcome.SetResult();
            }
            catch (AggregateException e)
            {
                outcome.SetException(e.InnerExceptions);
            }
        }) { IsBackground = true };
        thread.Start();
        return (started.Task.GetAwaiter().GetResult(), outcome.Task);
    }

    [Fact]
    public async Task Test_TheDatabaseIsReadOffTheCallingThread()
    {
        var recording = new ThreadRecordingProvider(Provider);
        var handler = new ReapplyCollectionOrderActionHandler(recording, Substitute.For<IDiagnosticManager>());

        var (caller, outcome) = RunFromADedicatedThread(() => handler.ExecuteAsync(Loadout, DiagnosticActions.ReapplyCollectionOrder(0xDEAD).Arguments));
        var act = () => outcome;
        await act.Should().ThrowAsync<CollectionOrderReapplyRefusedException>();

        recording.Threads.Should().NotBeEmpty().And.NotContain(caller, "a command runs its first part on the UI thread, so the read has to move off it");
    }
}
