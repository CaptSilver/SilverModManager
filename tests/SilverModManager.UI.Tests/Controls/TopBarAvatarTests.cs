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
                Substitute.For<IWindowNotificationService>(), Substitute.For<INexusCredentialSummary>(),
                new HttpClient());
            topBar.Activator.Activate();
            return Task.CompletedTask;
        });

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!logger.Entries.Any(e => e.Message.Contains("avatar.png")) && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        logger.Entries.Should().Contain(e => e.Message.Contains("avatar.png") && e.Level == LogLevel.Warning);
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task TheAvatarIsFetchedThroughTheInjectedHttpClient()
    {
        var handler = new RecordingHandler();
        var avatar = new Uri("http://avatars.invalid/avatar.png");

        await ActivateTopBar(avatar, new HttpClient(handler));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (handler.Requests.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        handler.Requests.Should().ContainSingle().Which.Should().Be(avatar);
    }

    [Fact]
    public async Task DeactivatingTheTopBarCancelsAnAvatarFetchInFlight()
    {
        var handler = new RecordingHandler { Hang = true };
        var topBar = await ActivateTopBar(new Uri("http://avatars.invalid/avatar.png"), new HttpClient(handler));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (handler.Requests.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        handler.Requests.Should().NotBeEmpty();

        await OnUi(() =>
        {
            topBar.Activator.Deactivate();
            return Task.CompletedTask;
        });

        deadline = DateTime.UtcNow.AddSeconds(10);
        while (!handler.Cancelled && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        handler.Cancelled.Should().BeTrue("the request token follows the activation");
    }

    private async Task<TopBarViewModel> ActivateTopBar(Uri avatar, HttpClient client)
    {
        var login = Substitute.For<ILoginManager>();
        login.AvatarObservable.Returns(Observable.Return<Uri?>(avatar));
        login.UserInfoObservable.Returns(R3.Observable.Never<UserInfo?>());
        login.IsLoggedInObservable.Returns(Observable.Never<bool>());
        login.UserRoleObservable.Returns(Observable.Never<UserRole>());

        TopBarViewModel? created = null;
        await OnUi(() =>
        {
            created = new TopBarViewModel(Substitute.For<IServiceProvider>(), new RecordingLogger(),
                login, Substitute.For<IWindowManager>(), Substitute.For<IOverlayController>(),
                Substitute.For<IOSInterop>(), Substitute.For<ISettingsManager>(), Substitute.For<IFileSystem>(),
                Substitute.For<IWindowNotificationService>(), Substitute.For<INexusCredentialSummary>(), client);
            created.Activator.Activate();
            return Task.CompletedTask;
        });
        return created ?? throw new InvalidOperationException("The top bar was not created");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public ConcurrentQueue<Uri?> Requests { get; } = new();
        public bool Hang { get; init; }
        public volatile bool Cancelled;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri);
            if (!Hang) return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }
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
