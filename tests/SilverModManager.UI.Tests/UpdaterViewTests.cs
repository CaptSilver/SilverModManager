using Avalonia.Controls;
using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Overlays.Updater;
using SilverModManager.Networking.GitHub.DTOs;
using SilverModManager.Sdk;

namespace SilverModManager.UI.Tests;

public class UpdaterViewTests(IServiceProvider provider) : AUiTest(provider), IDisposable
{
    private static readonly byte[] NewBytes = "new appimage"u8.ToArray();

    private readonly string _directory = Directory.CreateTempSubdirectory("updater-view-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class NoRestart : IAppRestarter
    {
        public void Restart(string appImagePath) => throw new InvalidOperationException("The view test never restarts.");
    }

    private UpdaterViewModel CreateViewModel()
    {
        var target = Path.Combine(_directory, "SilverModManager.AppImage");
        File.WriteAllText(target, "old");

        var digest = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(NewBytes));
        var asset = new Asset("https://api.example.com/asset", 1, "SilverModManager.AppImage", "application/octet-stream", "uploaded", NewBytes.Length, 0,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "https://example.com/SilverModManager.AppImage", digest);
        var installer = new AppImageUpdateInstaller(NullLogger.Instance, (_, destination, _, token) => File.WriteAllBytesAsync(destination, NewBytes, token));

        return new UpdaterViewModel(Substitute.For<IOSInterop>(), null!, InstallationMethod.AppImage, new Version("1.0.0"), new Version("2.0.0"),
            new Uri("https://example.com"), new Uri("https://example.com/SilverModManager.AppImage"),
            new InAppUpdate(installer, asset, target, new NoRestart(), NullLogger.Instance));
    }

    private static T Find<T>(UpdaterView view, string name) where T : Control => view.FindControl<T>(name) ?? throw new InvalidOperationException($"No control `{name}`");

    [Fact]
    public async Task Test_InAppUpdate_ShowsUpdateNowThenRestartNow()
    {
        var viewModel = await Dispatcher.UIThread.InvokeAsync(CreateViewModel);
        var view = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new UpdaterView { ViewModel = viewModel };
            new Window { Width = 800, Height = 600, Content = view }.Show();
            return view;
        });

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Find<StandardButton>(view, "ButtonUpdateNow").IsVisible.Should().BeTrue();
            Find<StandardButton>(view, "ButtonDownloadReleaseAssetInBrowser").IsVisible.Should().BeFalse("the app installs the update itself");
            Find<StandardButton>(view, "ButtonRestartNow").IsVisible.Should().BeFalse();
        });

        await Dispatcher.UIThread.InvokeAsync(viewModel.UpdateNowAsync);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Find<StandardButton>(view, "ButtonUpdateNow").IsVisible.Should().BeFalse();
            Find<StandardButton>(view, "ButtonRestartNow").IsVisible.Should().BeTrue();
            Find<TextBlock>(view, "TextStatus").IsVisible.Should().BeTrue();
            Find<TextBlock>(view, "TextStatus").Text.Should().Be(viewModel.StatusMessage);
        });
    }
}
