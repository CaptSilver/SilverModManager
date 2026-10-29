using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.App.UI.Controls.Settings.SettingEntries;
using SilverModManager.Networking.HttpDownloader;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.UI.Tests;

/// <summary>
/// The text box behind a free-text setting, built the way the Settings page builds it, for the HTTP proxy setting.
/// It's a UI view model, so it is built and used on the UI thread of the running test app: building it anywhere else
/// first would tie the shared dispatcher to the wrong thread and crash the whole test host.
/// </summary>
public class SettingTextControlTests(IServiceProvider provider) : AUiTest(provider)
{
    private ISettingsManager Settings => Provider.GetRequiredService<ISettingsManager>();

    private PropertyConfig ProxyProperty() => Settings.Configs[typeof(HttpProxySettings)].Properties.Single(p => p.Name == nameof(HttpProxySettings.HttpProxy));

    private SettingTextViewModel CreateViewModel()
    {
        var property = ProxyProperty();
        property.ContainerOptions.Should().BeOfType<TextContainerOptions>();
        return (SettingTextViewModel)new SettingTextFactory().Create(Provider, Settings, (TextContainerOptions)property.ContainerOptions!, property);
    }

    [Fact]
    public Task ItShowsTheCurrentValueOfTheSetting() => Dispatcher.UIThread.InvokeAsync(() =>
    {
        Settings.Update<HttpProxySettings>(s => s with { HttpProxy = "http://127.0.0.1:8888" });
        try
        {
            var viewModel = CreateViewModel();

            viewModel.TextContainer.CurrentValue.Should().Be("http://127.0.0.1:8888");
            viewModel.TextContainer.DefaultValue.Should().BeEmpty();
            viewModel.TextContainer.HasChanged.Should().BeFalse();
        }
        finally
        {
            Settings.Update<HttpProxySettings>(s => s with { HttpProxy = "" });
        }
    }).GetTask();

    [Fact]
    public Task EditingTheTextMarksItChanged_AndSavingWritesItToTheSetting() => Dispatcher.UIThread.InvokeAsync(() =>
    {
        var viewModel = CreateViewModel();
        try
        {
            viewModel.TextContainer.CurrentValue = "socks5://127.0.0.1:1080";

            viewModel.TextContainer.HasChanged.Should().BeTrue();
            Settings.Get<HttpProxySettings>().HttpProxy.Should().BeEmpty("nothing is written until the page saves");

            viewModel.TextContainer.Update(Settings);

            Settings.Get<HttpProxySettings>().HttpProxy.Should().Be("socks5://127.0.0.1:1080");
            viewModel.TextContainer.DefaultValue.Should().BeEmpty("saving a value must not change the declared default");
            viewModel.TextContainer.HasChanged.Should().BeFalse();
        }
        finally
        {
            Settings.Update<HttpProxySettings>(s => s with { HttpProxy = "" });
        }
    }).GetTask();

    [Fact]
    public Task ResettingToDefaultEmptiesTheText() => Dispatcher.UIThread.InvokeAsync(() =>
    {
        var viewModel = CreateViewModel();
        viewModel.TextContainer.CurrentValue = "http://proxy.example:3128";

        viewModel.TextContainer.ResetToDefault();

        viewModel.TextContainer.CurrentValue.Should().BeEmpty();
        viewModel.TextContainer.IsDefault.Should().BeTrue();
    }).GetTask();
}
