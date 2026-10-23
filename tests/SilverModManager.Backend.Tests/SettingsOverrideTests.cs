using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Backend.Tests;

public class SettingsOverrideTests
{
    public record OverriddenSettings : ISettings
    {
        public string First { get; init; } = "";
        public string Second { get; init; } = "";
        public static string SettingsId => "SilverModManager.Tests.OverriddenSettings";
        public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder) => settingsBuilder;
    }

    [Fact]
    public void Test_OverridesOfOneSettingAllApplyInRegistrationOrder()
    {
        var provider = new ServiceCollection()
            .AddLogging()
            .AddSettings<OverriddenSettings>()
            .OverrideSettingsForTests<OverriddenSettings>(settings => settings with { First = "from the profile", Second = "from the profile" })
            .OverrideSettingsForTests<OverriddenSettings>(settings => settings with { Second = "from the test" })
            .BuildServiceProvider();

        var overridden = new SettingsManager(provider).Get<OverriddenSettings>(key: null);

        overridden.First.Should().Be("from the profile");
        overridden.Second.Should().Be("from the test");
    }
}
