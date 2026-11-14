using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.App.UI.Controls.Settings.SettingEntries;
using SilverModManager.App.UI.Input.Bindings;
using SilverModManager.App.UI.Pages.Settings;
using SilverModManager.App.UI.Windows;
using SilverModManager.Platform.Gamepad;
using SilverModManager.UI.Sdk.Settings;
using SilverModManager.UI.Tests.Framework;
using SilverModManager.UI.Tests.Input;

namespace SilverModManager.UI.Tests;

[Collection(HeadlessUi.Collection)]
public class SettingsViewTests(IServiceProvider provider) : AUiTest(provider)
{
    private SettingsPageViewModel NewPage() =>
        ActivatorUtilities.CreateInstance<SettingsPageViewModel>(Provider, Substitute.For<IWindowManager>());

    [Fact]
    public async Task ADiscardOnAnotherTabIsNotUndoneWhenTheComboComesBack()
    {
        SettingsPageViewModel vm = null!;
        SettingsView view = null!;
        ISettingEntryViewModel language = null!;
        SingleValueMultipleChoiceContainer container = null!;
        object stored = null!;

        // Two settings can offer the same number of choices, so find the row's own view, not a ComboBox by item count.
        // Rows rebuild their views when a tab comes back and the old ones fade out for a moment, so a view counts only
        // once exactly one is left.
        ComboBox ComboOf()
        {
            var shown = view.GetVisualDescendants().OfType<SettingComboBoxView>()
                .Where(v => ReferenceEquals(v.ViewModel, language.InteractionControlViewModel))
                .ToList();
            shown.Should().ContainSingle("one view shows the Language row");
            return shown[0].GetVisualDescendants().OfType<ComboBox>().Single();
        }

        await OnUi(async () =>
        {
            vm = NewPage();
            view = new SettingsView { DataContext = vm };
            var window = Track(new Window { Content = view, Width = 1600, Height = 1000 });
            ShowRendered(window);
            language = vm.SettingEntries.Single(e => e.Config.Options.DisplayName == "Language");
            container = (SingleValueMultipleChoiceContainer)language.InteractionControlViewModel.ValueContainer;
            stored = container.CurrentValue;

            var combo = ComboOf();
            combo.SelectedIndex = combo.SelectedIndex == 0 ? 1 : 0;
            container.HasChanged.Should().BeTrue();

            var tabs = view.FindControl<TabControl>("Tabs")!; // NullForgivingGuardTests scans src only
            tabs.SelectedIndex = 1;
            await WindowHost.Flush(); // the General views deactivate
            vm.CancelCommand.Execute(R3.Unit.Default);
            tabs.SelectedIndex = 0; // and come back
        });

        await EventuallyOnUi(() =>
        {
            container.HasChanged.Should().BeFalse();
            container.CurrentValue.Should().Be(stored);
            ComboOf().SelectedIndex.Should().Be(((ISettingComboBoxViewModel)language.InteractionControlViewModel).SelectedItemIndex,
                "the view model's index follows the container; the view must show it");
        });
    }

    [Fact]
    public async Task DPadUpFromTheFirstRowReachesTheSelectedTabsHeaderWithoutSwitchingTabs()
    {
        await OnUi(() =>
        {
            var view = new SettingsView { DataContext = NewPage() };
            using var rig = new PadRig(view, () => [new FocusMoveBinding(), new TabCycleBinding(true), new TabCycleBinding(false)]);
            rig.Window.Width = 1600;
            rig.Window.Height = 1000;
            rig.Show();
            var tabs = view.FindControl<TabControl>("Tabs")!;
            var third = Math.Min(2, tabs.ItemCount - 1);
            tabs.SelectedIndex = third;
            rig.Window.UpdateLayout();
            PadRig.Drain();

            // The TabItem holds only the header; the content sits in the TabControl's PART_SelectedContentHost.
            var firstControl = ((SettingsTabView)tabs.SelectedContent!).GetVisualDescendants().OfType<InputElement>()
                .First(c => c.Focusable && c.IsEffectivelyEnabled);
            firstControl.Focus(NavigationMethod.Tab);

            rig.Send(new ButtonDown(TimeSpan.Zero, GamepadButton.DPadUp));
            rig.Window.UpdateLayout();
            PadRig.Drain();

            tabs.SelectedIndex.Should().Be(third, "moving up must not switch tabs");
            rig.Focused.Should().BeSameAs(tabs.ContainerFromIndex(third));
        });
    }
}
