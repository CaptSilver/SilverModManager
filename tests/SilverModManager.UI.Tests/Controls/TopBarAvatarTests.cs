using System.Collections.Concurrent;
using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Controls.TopBar;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Windows;
using SilverModManager.Sdk;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Sdk.Settings;
using SilverModManager.UI.Sdk;

namespace SilverModManager.UI.Tests.Controls;

/// <summary>
/// The account avatar is decoration: when it can't be fetched the top bar shows no picture, and the failure is a
/// warning in the log, not an error the app turns into a "Something went wrong" dialog.
/// </summary>
public class TopBarAvatarTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task AnAvatarThatCannotBeFetchedIsAWarningNotAnError()
    {
        var logger = new RecordingLogger();
        var login = Substitute.For<ILoginManager>();
        // Port 1 on loopback refuses the connection at once.
        login.AvatarObservable.Returns(Observable.Return<Uri?>(new Uri("http://127.0.0.1:1/avatar.png")));
        login.UserInfoObservable.Returns(R3.Observable.Never<UserInfo?>());
        login.IsLoggedInObservable.Returns(Observable.Never<bool>());
        login.UserRoleObservable.Returns(Observable.Never<UserRole>());

        await OnUi(() =>
        {
            var topBar = new TopBarViewModel(Substitute.For<IServiceProvider>(), logger,
                login, Substitute.For<IWindowManager>(), Substitute.For<IOverlayController>(),
                Substitute.For<IOSInterop>(), Substitute.For<ISettingsManager>(), Substitute.For<IFileSystem>(),
                Substitute.For<IWindowNotificationService>(), Substitute.For<INexusCredentialSummary>());
            topBar.Activator.Activate();
            return Task.CompletedTask;
        });

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!logger.Entries.Any(e => e.Message.Contains("avatar.png")) && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        logger.Entries.Should().Contain(e => e.Message.Contains("avatar.png") && e.Level == LogLevel.Warning);
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    private sealed class RecordingLogger : ILogger<TopBarViewModel>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
