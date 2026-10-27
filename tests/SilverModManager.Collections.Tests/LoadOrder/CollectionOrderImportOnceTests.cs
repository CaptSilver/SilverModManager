using System.Reflection;
using System.Runtime.ExceptionServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FOMOD;
using SilverModManager.Games.TestFramework;
using Xunit;

namespace SilverModManager.Collections.Tests.LoadOrder;

/// <summary>
/// A whole-collection install runs one download job per mod; only the collection job may import the order, once, after
/// all of them, or each mod's job would re-deal the block while the others are still installing.
/// </summary>
public class CollectionOrderImportOnceTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<CollectionOrderImportOnceTests>(helper)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private CountingLoadoutManager? _counter;

    /// <summary>Forwards every call to the real loadout manager and counts SetGroupOrder, which the sink calls once per order it applies.</summary>
    public class CountingLoadoutManager : DispatchProxy
    {
        private ILoadoutManager _inner = null!;
        private int _setGroupOrderCalls;

        public int SetGroupOrderCalls => Volatile.Read(ref _setGroupOrderCalls);

        public static ILoadoutManager Wrap(ILoadoutManager inner, out CountingLoadoutManager counter)
        {
            var proxy = Create<ILoadoutManager, CountingLoadoutManager>();
            counter = (CountingLoadoutManager)(object)proxy;
            counter._inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(ILoadoutManager.SetGroupOrder)) Interlocked.Increment(ref _setGroupOrderCalls);
            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
    }

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddSingleton(provider => new DefaultPriorityOrderSink(
            CountingLoadoutManager.Wrap(provider.GetRequiredService<ILoadoutManager>(), out _counter),
            provider.GetRequiredService<ILogger<DefaultPriorityOrderSink>>()));

    [Fact]
    public async Task InstallingAWholeCollectionAppliesItsOrderOnce()
    {
        var mods = Enumerable.Range(0, 3).Select(i => ((FomodOption[]?)null, $"Plain mod {i}")).ToArray();
        var (file, revision, items) = await new OfflineCollections(ServiceProvider, Connection, LibraryService, TemporaryFileManager)
            .Create(mods, silverLoadOrder: m => [..m.Select(mod => new SilverLoadOrderEntry { Md5 = mod.Source.Md5, Name = mod.Name })]);
        var loadout = await CreateLoadout();

        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        async Task<NexusCollectionLoadoutGroup.ReadOnly> Run() => await job;
        await Run().WaitAsync(Timeout);

        _counter.Should().NotBeNull("the install resolves the default sink");
        _counter!.SetGroupOrderCalls.Should().Be(1);
    }
}
