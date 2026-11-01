using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.App.UI.Windows;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The library page asks every mod source for updates: entering the page lets a source skip a fresh cache, the
/// Refresh button does not.
/// </summary>
public class LibraryRefreshUpdatesTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class RecordingSource(string name, Exception? failure = null) : IModSource
    {
        public ConcurrentQueue<(GameId Game, bool Force)> Checks { get; } = new();

        public string DisplayName => name;
        public bool Owns(LibraryItem.ReadOnly item) => false;
        public Uri? PageUri(LibraryItem.ReadOnly item) => null;
        public Uri? ChangelogUri(LibraryItem.ReadOnly item) => null;

        public Task CheckUpdates(IGameData game, bool force, CancellationToken cancellationToken)
        {
            Checks.Enqueue((game.GameId, force));
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }
    }

    /// <summary>The host's services with the mod source resolver swapped for one over the test's sources.</summary>
    private sealed class WithSources(IServiceProvider inner, params IModSource[] sources) : IServiceProvider, IKeyedServiceProvider
    {
        private readonly ModSourceResolver _resolver = new(sources);

        public object? GetService(Type serviceType) => serviceType == typeof(ModSourceResolver) ? _resolver : inner.GetService(serviceType);
        public object? GetKeyedService(Type serviceType, object? serviceKey) => ((IKeyedServiceProvider)inner).GetKeyedService(serviceType, serviceKey);
        public object GetRequiredKeyedService(Type serviceType, object? serviceKey) => ((IKeyedServiceProvider)inner).GetRequiredKeyedService(serviceType, serviceKey);
    }

    private async Task<(LibraryViewModel Vm, GameId Game)> CreateViewModel(params IModSource[] sources)
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Refresh updates");
        var vm = await OnUi(() => Task.FromResult(new LibraryViewModel(
            Substitute.For<IWindowManager>(),
            new WithSources(Provider, sources),
            Provider.GetRequiredService<IGameDomainToGameIdMappingCache>(),
            loadout.LoadoutId)));
        return (vm, loadout.InstallationInstance.Game.GameId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheRefreshCommand_AsksEverySourceWithItsForceArgument(bool force)
    {
        var first = new RecordingSource("first");
        var second = new RecordingSource("second");
        var (vm, game) = await CreateViewModel(first, second);

        vm.RefreshUpdatesCommand.Execute(force);

        await Eventually(() =>
        {
            first.Checks.Should().Equal((game, force));
            second.Checks.Should().Equal((game, force));
        });
    }

    // Drives the real button: whatever LibraryView bound to it is what the click would send.
    [Fact]
    public async Task TheRefreshButton_ForcesTheCheck()
    {
        var source = new RecordingSource("source");
        var (vm, game) = await CreateViewModel(source);

        await OnUi(() =>
        {
            var view = new LibraryView { ViewModel = vm };
            var window = Track(new Window { Width = 1200, Height = 800, Content = view });
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var button = view.FindControl<StandardButton>("Refresh")!;
            button.Command.Should().NotBeNull();
            button.Command!.Execute(button.CommandParameter);
            return Task.FromResult(0);
        });

        await Eventually(() => source.Checks.Should().Contain((game, true)));
    }

    [Fact]
    public async Task EnteringThePage_RefreshesWithoutForce()
    {
        var source = new RecordingSource("source");
        var (vm, game) = await CreateViewModel(source);

        using var _ = await OnUi(() => Task.FromResult(vm.Activator.Activate()));

        await Eventually(() => source.Checks.Should().Equal((game, false)));
    }

    [Fact]
    public async Task ASourceThatFails_DoesNotStopTheOthersChecking()
    {
        var broken = new RecordingSource("broken", new HttpRequestException("offline"));
        var healthy = new RecordingSource("healthy");
        var (vm, game) = await CreateViewModel(broken, healthy);

        vm.RefreshUpdatesCommand.Execute(true);

        await Eventually(() => healthy.Checks.Should().Equal((game, true)));
    }

    // An HttpClient timeout is a TaskCanceledException although nobody cancelled the command.
    [Fact]
    public async Task ASourceThatTimesOut_DoesNotStopTheOthersChecking()
    {
        var timedOut = new RecordingSource("timed out", new TaskCanceledException("The request timed out"));
        var healthy = new RecordingSource("healthy");
        var (vm, game) = await CreateViewModel(timedOut, healthy);

        vm.RefreshUpdatesCommand.Execute(true);

        await Eventually(() => healthy.Checks.Should().Equal((game, true)));
    }
}
