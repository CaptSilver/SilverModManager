using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.App.UI.Controls.Settings.SettingEntries;
using SilverModManager.App.UI.Controls.Settings.Section;
using SilverModManager.App.UI.Pages.Settings;
using SilverModManager.App.UI.Settings;
using SilverModManager.App.UI.Windows;
using SilverModManager.Sdk.Settings;
using SilverModManager.UI.Sdk.Settings;

namespace SilverModManager.UI.Tests;

public class SettingsPageViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    // The combo rows bind their view model on construction, which the app does on the UI thread.
    private Task<SettingsPageViewModel> NewPage() => Dispatcher.UIThread.InvokeAsync(() =>
        ActivatorUtilities.CreateInstance<SettingsPageViewModel>(Provider, Substitute.For<IWindowManager>())).GetTask();

    private static SettingToggleViewModel Toggle(SettingsPageViewModel page, string name) =>
        (SettingToggleViewModel)page.SettingEntries.Single(e => e.Config.Options.DisplayName == name).InteractionControlViewModel;

    [Fact]
    public async Task AnEditIsSeenWithoutTheViewEverShowingThePage()
    {
        var page = await NewPage();
        var toggle = Toggle(page, "Bring app window to front");

        page.HasAnyValueChanged.Value.Should().BeFalse();
        toggle.BooleanContainer.CurrentValue = !toggle.BooleanContainer.CurrentValue;
        page.HasAnyValueChanged.Value.Should().BeTrue();

        page.CancelCommand.Execute(R3.Unit.Default);
        toggle.BooleanContainer.HasChanged.Should().BeFalse();
        page.HasAnyValueChanged.Value.Should().BeFalse();
    }

    [Fact]
    public async Task SaveWritesTheEditedEntry()
    {
        var settings = Provider.GetRequiredService<ISettingsManager>();
        var before = settings.Get<BehaviorSettings>();
        try
        {
            var page = await NewPage();
            var toggle = Toggle(page, "Bring app window to front");
            toggle.BooleanContainer.CurrentValue = !before.BringWindowToFront;

            page.SaveCommand.Execute(R3.Unit.Default);

            settings.Get<BehaviorSettings>().BringWindowToFront.Should().Be(!before.BringWindowToFront);
            page.HasAnyValueChanged.Value.Should().BeFalse();
        }
        finally
        {
            settings.Set(before); // the store is shared with every other UI test
        }
    }

    [Fact]
    public async Task TheTabsAreGeneralAppearanceDownloadsAccountsAndDeveloperInThatOrderWithNoEmptyTab()
    {
        var page = await NewPage();
        page.Tabs.Select(t => t.Tab).Should().Equal(SettingsTab.General, SettingsTab.Appearance, SettingsTab.Downloads, SettingsTab.Accounts, SettingsTab.Developer);
        page.Tabs.Should().OnlyContain(t => t.Entries.Count > 0);
    }

    [Fact]
    public async Task TheAccountsTabShowsSectionHeadersAndTheGeneralTabDoesNot()
    {
        var page = await NewPage();
        page.Tabs.Single(t => t.Tab == SettingsTab.General).Items.Should().NotContain(i => i is ISettingSectionViewModel);
        var accounts = page.Tabs.Single(t => t.Tab == SettingsTab.Accounts);
        accounts.Items.OfType<ISettingSectionViewModel>().Select(s => s.Descriptor.Id).Should().Equal(Sections.Accounts, Sections.Advanced);
    }

    [Fact]
    public async Task AnEditCountsOnItsOwnTabAndOnNoOther()
    {
        var page = await NewPage();
        var toggle = Toggle(page, "Bring app window to front");
        toggle.BooleanContainer.CurrentValue = !toggle.BooleanContainer.CurrentValue;

        page.Tabs.Single(t => t.Tab == SettingsTab.General).ChangedCount.Value.Should().Be(1);
        page.Tabs.Where(t => t.Tab != SettingsTab.General).Should().OnlyContain(t => t.ChangedCount.Value == 0);

        page.CancelCommand.Execute(R3.Unit.Default);
        page.Tabs.Should().OnlyContain(t => t.ChangedCount.Value == 0);
    }
}
