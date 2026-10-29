using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Backend.Tests;

public class SettingsPropertyUpdateTests
{
    public record ProxyLikeSettings : ISettings
    {
        public string Value { get; init; } = "";
        public static string SettingsId => "SilverModManager.Tests.ProxyLikeSettings";

        public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder) => settingsBuilder
            .ConfigureProperty(x => x.Value, new PropertyOptions<ProxyLikeSettings, string>
            {
                Section = SectionId.DefaultValue,
                DisplayName = "Value",
                DescriptionFactory = _ => "A value",
            }, new TextContainerOptions());
    }

    private static (SettingsManager Manager, PropertyConfig Property) CreateManager()
    {
        var provider = new ServiceCollection().AddLogging().AddSettings<ProxyLikeSettings>().BuildServiceProvider();
        var manager = new SettingsManager(provider);
        return (manager, manager.Configs[typeof(ProxyLikeSettings)].Properties.Single());
    }

    [Fact]
    public void Test_UpdatingAPropertyOfAnUnsavedSetting_LeavesTheDeclaredDefaultAlone()
    {
        var (manager, property) = CreateManager();

        property.Update(manager, "socks5://127.0.0.1:1080");

        manager.Get<ProxyLikeSettings>(key: null).Value.Should().Be("socks5://127.0.0.1:1080");
        property.GetDefaultValueCasted<string>(manager).Should().BeEmpty();
        manager.GetDefault<ProxyLikeSettings>().Value.Should().BeEmpty();
    }

    [Fact]
    public void Test_UpdatingAPropertyNotifiesWithANewSettingsObject()
    {
        var (manager, property) = CreateManager();
        var before = manager.Get<ProxyLikeSettings>(key: null);

        property.Update(manager, "changed");

        before.Value.Should().BeEmpty("the object handed out earlier is never edited in place");
        manager.Get<ProxyLikeSettings>(key: null).Should().NotBeSameAs(before);
    }
}
