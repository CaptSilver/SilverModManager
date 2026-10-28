using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Files.Diff;
using SilverModManager.App.UI.Pages.Diff.ApplyDiff;
using SilverModManager.App.UI.Windows;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.UI.Tests;

/// <summary>
/// Building the diff tree walks the install and the loadout, so the Apply Diff page must not do it on the UI thread,
/// neither when it opens nor when Refresh is pressed.
/// </summary>
public class ApplyDiffViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task TheDiffTreeIsBuiltOffTheUiThreadOnOpenAndOnRefresh()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Apply Diff Off UI");
        var onUiThread = new List<bool>();
        var sync = Substitute.For<ISynchronizerService>();
        sync.GetApplyDiffTree(Arg.Any<LoadoutId>()).Returns(_ =>
        {
            lock (onUiThread) onUiThread.Add(Dispatcher.UIThread.CheckAccess());
            return FileDiffTree.Create([]);
        });

        var services = new ServiceCollection()
            .AddSingleton(sync)
            .AddSingleton(Connection)
            .AddLogging()
            .BuildServiceProvider();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var page = new ApplyDiffViewModel(Substitute.For<IWindowManager>(), services);
            page.Initialize(loadout.LoadoutId);
            _ = page.RefreshCommand.Execute();
        });

        await Eventually(() =>
        {
            lock (onUiThread) onUiThread.Should().HaveCount(2);
        });
        lock (onUiThread) onUiThread.Should().AllBeEquivalentTo(false);
    }

    private sealed class RecordingLogger : ILogger<ApplyDiffViewModel>
    {
        public List<(LogLevel Level, Exception? Error)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, exception));
        }
    }

    [Fact]
    public async Task AFailureBuildingTheFirstDiffTreeIsLogged()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Apply Diff Failure");
        var sync = Substitute.For<ISynchronizerService>();
        sync.GetApplyDiffTree(Arg.Any<LoadoutId>()).Returns(_ => throw new KeyNotFoundException("no such loadout"));
        var logger = new RecordingLogger();

        var services = new ServiceCollection()
            .AddSingleton(sync)
            .AddSingleton(Connection)
            .AddSingleton<ILogger<ApplyDiffViewModel>>(logger)
            .BuildServiceProvider();

        await Dispatcher.UIThread.InvokeAsync(() =>
            new ApplyDiffViewModel(Substitute.For<IWindowManager>(), services).Initialize(loadout.LoadoutId));

        await Eventually(() =>
        {
            lock (logger.Entries) logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.Error is KeyNotFoundException);
        });
    }

    [Fact]
    public async Task AFailureBuildingTheFirstDiffTreeShowsAFailureInsteadOfABlankPage()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Apply Diff Failure Line");
        var sync = Substitute.For<ISynchronizerService>();
        sync.GetApplyDiffTree(Arg.Any<LoadoutId>()).Returns(_ => throw new KeyNotFoundException("no such loadout"));

        var services = new ServiceCollection()
            .AddSingleton(sync)
            .AddSingleton(Connection)
            .AddLogging()
            .BuildServiceProvider();

        var page = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var vm = new ApplyDiffViewModel(Substitute.For<IWindowManager>(), services);
            vm.Initialize(loadout.LoadoutId);
            return vm;
        });

        await Eventually(() => page.LoadFailed.Should().BeTrue());
        page.BodyViewModel.Should().BeNull();
    }

    [Fact]
    public async Task ARefreshThatSucceedsAfterAFailureClearsTheFailure()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Apply Diff Recovers");
        var fail = true;
        var sync = Substitute.For<ISynchronizerService>();
        sync.GetApplyDiffTree(Arg.Any<LoadoutId>()).Returns(_ => fail ? throw new KeyNotFoundException("no such loadout") : FileDiffTree.Create([]));

        var services = new ServiceCollection()
            .AddSingleton(sync)
            .AddSingleton(Connection)
            .AddLogging()
            .BuildServiceProvider();

        var page = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var vm = new ApplyDiffViewModel(Substitute.For<IWindowManager>(), services);
            vm.Initialize(loadout.LoadoutId);
            return vm;
        });
        await Eventually(() => page.LoadFailed.Should().BeTrue());

        fail = false;
        await Dispatcher.UIThread.InvokeAsync(() => _ = page.RefreshCommand.Execute());

        await Eventually(() => page.LoadFailed.Should().BeFalse());
        await Eventually(() => page.BodyViewModel.Should().NotBeNull());
    }
}
