using FluentAssertions;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Sdk.Tests.Settings;

public class SettingsIdsTests
{
    private sealed class Sample : ISettings
    {
        public static string SettingsId => "SilverModManager.Tests.Sample";
        public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder) => settingsBuilder;
    }

    [Fact]
    public void Test_WithoutKeyIsTheId()
    {
        SettingsIds.For<Sample>(null, '_').Should().Be("SilverModManager.Tests.Sample");
        SettingsIds.For<Sample>("  ", '|').Should().Be("SilverModManager.Tests.Sample");
    }

    [Fact]
    public void Test_KeyIsJoinedWithSeparator()
    {
        SettingsIds.For<Sample>("a", '_').Should().Be("SilverModManager.Tests.Sample_a");
        SettingsIds.For<Sample>("a", '|').Should().Be("SilverModManager.Tests.Sample|a");
    }
}
