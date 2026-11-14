using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.App.UI.Controls.Settings.SettingEntries;
using SilverModManager.App.UI.Pages.Settings;

namespace SilverModManager.UI.Tests;

public class SettingComboBoxViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task AnEditMadeWhileActiveIsNotReplayedAfterADiscardThatHappenedWhileInactive()
    {
        // The view model binds with BindToVM, which wants the UI thread, as the app constructs it there.
        await OnUi(() =>
        {
            var page = Provider.GetRequiredService<ISettingsPageViewModel>();
            var combo = (SettingComboBoxViewModel)page.SettingEntries
                .Single(e => e.Config.Options.DisplayName == "Language").InteractionControlViewModel;
            var container = combo.SingleValueMultipleChoiceContainer;
            var storedIndex = combo.SelectedItemIndex;
            var otherIndex = storedIndex == 0 ? 1 : 0;

            var active = combo.Activator.Activate();
            combo.SelectedItemIndex = otherIndex;
            container.HasChanged.Should().BeTrue("the pick reached the container");
            active.Dispose(); // the tab is hidden

            container.ResetToPrevious(); // Discard pressed on another tab
            container.HasChanged.Should().BeFalse();

            using var _ = combo.Activator.Activate(); // back to this tab
            container.HasChanged.Should().BeFalse("reactivation must not replay the old pick");
            combo.SelectedItemIndex.Should().Be(storedIndex);
        });
    }
}
