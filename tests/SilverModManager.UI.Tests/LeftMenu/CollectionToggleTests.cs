using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Tests.Pages.Collections;
using ReactiveUI;

namespace SilverModManager.UI.Tests.LeftMenu;

/// <summary>
/// The switch on an installed collection's left-menu entry. The ToggleSwitch writes its new state into IsEnabled
/// before its command runs, so the command must not depend on what IsEnabled holds: it flips the state the database has.
/// </summary>
public class CollectionToggleTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class OverridingServiceProvider(IServiceProvider inner, params (Type Type, object Instance)[] overrides) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            foreach (var (type, instance) in overrides)
                if (type == serviceType) return instance;
            return inner.GetService(serviceType);
        }
    }

    private static IWorkspaceController Controller()
    {
        var workspace = Substitute.For<IWorkspaceViewModel>();
        workspace.Id.Returns(WorkspaceId.From(Guid.NewGuid()));
        workspace.Panels.Returns(new ReadOnlyObservableCollection<IPanelViewModel>([]));
        var controller = Substitute.For<IWorkspaceController>();
        controller.ActiveWorkspace.Returns(workspace);
        return controller;
    }

    /// <summary>The entry as the menu has it: activated, so IsEnabled follows the database like the switch shows it.</summary>
    private CollectionLeftMenuItemViewModel Entry(EntityId group, LoadoutId loadout, IServiceProvider? services = null)
    {
        var entry = new CollectionLeftMenuItemViewModel(Controller(), WorkspaceId.From(Guid.NewGuid()),
            Provider.GetRequiredService<CollectionProviders>().For(CollectionGroup.Load(Connection.Db, group), loadout),
            services ?? Provider, CollectionGroupId.From(group));
        _activations.Add(entry.Activator.Activate());
        return entry;
    }

    private readonly List<IDisposable> _activations = [];

    private Task Switched(CollectionLeftMenuItemViewModel entry, bool on) => Eventually(() => entry.IsEnabled.Should().Be(on));

    public override async ValueTask DisposeAsync()
    {
        foreach (var activation in _activations) activation.Dispose();
        await base.DisposeAsync();
    }

    private bool IsDisabled(EntityId group) => LoadoutItem.Load(Connection.Db, group).IsDisabled;

    private async Task<(EntityId Group, LoadoutId Loadout)> Seed(string kind)
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"{kind} toggle {Guid.NewGuid()}");
        if (kind == "Wabbajack")
            return ((await CollectionSeed.AddModlistGroup(Connection, loadout.LoadoutId, "Toggled modlist", finished: true)).Id, loadout.LoadoutId);
        var revision = await CollectionSeed.AddRevision(Connection, loadout.InstallationInstance.Game.NexusModsGameId.Value, "Toggled collection");
        return ((await CollectionSeed.AddInstalledGroup(Connection, loadout.LoadoutId, "Toggled collection", revision)).Id, loadout.LoadoutId);
    }

    private Task Click(CollectionLeftMenuItemViewModel entry, bool bindingWritesIsEnabled) =>
        OnUi(async () =>
        {
            // What the two-way IsChecked binding does when the user clicks, before the command runs.
            if (bindingWritesIsEnabled) entry.IsEnabled = !entry.IsEnabled;
            await entry.ToggleIsEnabledCommand.Execute().ToTask();
        });

    [Theory]
    [InlineData("Nexus")]
    [InlineData("Wabbajack")]
    public async Task ClickingAnEnabledCollectionSwitchesItOff(string kind)
    {
        var (group, loadout) = await Seed(kind);
        var entry = Entry(group, loadout);
        await Switched(entry, on: true);

        await Click(entry, bindingWritesIsEnabled: true);

        IsDisabled(group).Should().BeTrue();
    }

    [Theory]
    [InlineData("Nexus")]
    [InlineData("Wabbajack")]
    public async Task ClickingADisabledCollectionSwitchesItOn(string kind)
    {
        var (group, loadout) = await Seed(kind);
        var entry = Entry(group, loadout);
        await Click(entry, bindingWritesIsEnabled: true);
        await Eventually(() => IsDisabled(group).Should().BeTrue());
        await Switched(entry, on: false);

        await Click(entry, bindingWritesIsEnabled: true);

        IsDisabled(group).Should().BeFalse();
    }

    [Fact]
    public async Task TheResultDoesNotDependOnWhatIsEnabledHolds()
    {
        var (group, loadout) = await Seed("Nexus");
        var entry = Entry(group, loadout);
        await Switched(entry, on: true);

        await Click(entry, bindingWritesIsEnabled: false);

        IsDisabled(group).Should().BeTrue();
    }

    [Fact]
    public async Task AToggleTheServiceRefusesShowsItsReason()
    {
        var (group, loadout) = await Seed("Nexus");
        var item = LoadoutItem.Load(Connection.Db, group);
        var manager = Substitute.For<ILoadoutManager>();
        manager.SetGroupsEnabled(Arg.Any<LoadoutId>(), Arg.Any<IReadOnlyList<LoadoutItemId>>(), Arg.Any<bool>())
            .Returns(new ValueTask<ToggleResult>(new ToggleResult([], [(item.LoadoutItemId, "it is a required item of a collection")])));
        var toasts = Substitute.For<IWindowNotificationService>();
        var entry = Entry(group, loadout, new OverridingServiceProvider(Provider, (typeof(ILoadoutManager), manager), (typeof(IWindowNotificationService), toasts)));

        await Switched(entry, on: true);

        await Click(entry, bindingWritesIsEnabled: true);

        toasts.Received(1).ShowToast(Arg.Is<string>(message => message.Contains("it is a required item of a collection")),
            Arg.Any<ToastNotificationVariant>(), Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>());
    }

    private async Task<(Window Window, ToggleSwitch Toggle)> ViewOf(CollectionLeftMenuItemViewModel entry)
    {
        var (window, toggle) = await OnUi(() => Task.FromResult(Build()));
        (Window, ToggleSwitch) Build()
        {
            var view = new LeftMenuItemView { ViewModel = entry };
            var w = Track(new Window { Content = view, Width = 300, Height = 60 });
            ShowRendered(w);
            return (w, view.GetVisualDescendants().OfType<ToggleSwitch>().Single());
        }
        return (window, toggle);
    }

    private Task ClickSwitch(Window window, ToggleSwitch toggle) =>
        OnUi(() =>
        {
            Render(window);
            var center = toggle.TranslatePoint(new Point(toggle.Bounds.Width / 2, toggle.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
        });

    [Fact]
    public async Task ClickingTheSwitchInTheViewSwitchesTheCollectionOff()
    {
        var (group, loadout) = await Seed("Nexus");
        var entry = Entry(group, loadout);
        await Switched(entry, on: true);
        var (window, toggle) = await ViewOf(entry);

        await ClickSwitch(window, toggle);

        await Eventually(() => IsDisabled(group).Should().BeTrue());
    }

    [Fact]
    public async Task ClickingTheSwitchOfARefusedToggleInTheViewPutsTheSwitchBack()
    {
        var (group, loadout) = await Seed("Nexus");
        var item = LoadoutItem.Load(Connection.Db, group);
        var manager = Substitute.For<ILoadoutManager>();
        manager.SetGroupsEnabled(Arg.Any<LoadoutId>(), Arg.Any<IReadOnlyList<LoadoutItemId>>(), Arg.Any<bool>())
            .Returns(new ValueTask<ToggleResult>(new ToggleResult([], [(item.LoadoutItemId, "it is a required item of a collection")])));
        var toasts = Substitute.For<IWindowNotificationService>();
        var entry = Entry(group, loadout, new OverridingServiceProvider(Provider, (typeof(ILoadoutManager), manager), (typeof(IWindowNotificationService), toasts)));
        await Switched(entry, on: true);
        var (window, toggle) = await ViewOf(entry);
        await OnUi(() => { toggle.IsChecked.Should().BeTrue(); return Task.CompletedTask; });

        await ClickSwitch(window, toggle);

        await manager.Received(1).SetGroupsEnabled(Arg.Any<LoadoutId>(), Arg.Any<IReadOnlyList<LoadoutItemId>>(), enabled: false);
        await EventuallyOnUi(() => toggle.IsChecked.Should().BeTrue("the refused click changed nothing, so the switch goes back"));
        entry.IsEnabled.Should().BeTrue();
        IsDisabled(group).Should().BeFalse();
    }
}
