using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.App.UI.Settings;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.UI.Tests.Pages;

public class AdultContentSettingTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public void AdultContent_IsHiddenByDefault()
    {
        new AdultContentSettings().ShowAdultContent.Should().BeFalse();
        new SettingsAdultContentSetting(Provider.GetRequiredService<ISettingsManager>()).ShowAdultContent.Should().BeFalse();
    }

    [Fact]
    public void TheSetting_FollowsTheStoredValueAndReportsEachFlip()
    {
        var settings = Provider.GetRequiredService<ISettingsManager>();
        var setting = new SettingsAdultContentSetting(settings);
        var seen = new List<bool>();
        using var subscription = setting.Changes.Subscribe(seen.Add);

        settings.Update<AdultContentSettings>(s => s with { ShowAdultContent = true });
        settings.Update<AdultContentSettings>(s => s with { ShowAdultContent = true });
        settings.Update<AdultContentSettings>(s => s with { ShowAdultContent = false });

        setting.ShowAdultContent.Should().BeFalse();
        // Replays the current value on subscribing, then only real changes.
        seen.Should().Equal(false, true, false);
    }
}
