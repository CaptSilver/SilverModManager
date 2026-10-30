using FluentAssertions;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Backend.Tests;

public class SettingsCastTests
{
    public record WantedSettings : ISettings
    {
        public static string SettingsId => "SilverModManager.Tests.WantedSettings";
        public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder) => settingsBuilder;
    }

    [Fact]
    public void Expect_ReturnsAValueOfTheRequestedType()
    {
        var value = new WantedSettings();

        SettingsManager.Expect<WantedSettings>(new SettingKey(typeof(WantedSettings), null), value).Should().BeSameAs(value);
    }

    [Fact]
    public void Expect_NamesTheExpectedAndActualTypeWhenTheStoredValueIsSomethingElse()
    {
        var key = new SettingKey(typeof(WantedSettings), "profile");

        var cast = () => SettingsManager.Expect<WantedSettings>(key, "not a settings object");

        cast.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(nameof(WantedSettings)).And.Contain("System.String").And.Contain("profile");
    }
}
