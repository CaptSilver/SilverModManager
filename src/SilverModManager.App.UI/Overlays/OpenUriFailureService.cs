using Avalonia.Threading;
using JetBrains.Annotations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SilverModManager.App.UI.Overlays.Generic.MessageBox.Ok;
using SilverModManager.Sdk;

namespace SilverModManager.App.UI.Overlays;

/// <summary>
/// Shows the link in a dialog with a Copy button when no handler could open it, so every "open in browser"
/// action ends in something the user can act on instead of nothing happening.
/// </summary>
[UsedImplicitly]
internal sealed class OpenUriFailureService(IOSInterop osInterop, IServiceProvider serviceProvider, ILogger<OpenUriFailureService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        osInterop.OpenUriFailed += OnOpenUriFailed;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        osInterop.OpenUriFailed -= OnOpenUriFailed;
        return Task.CompletedTask;
    }

    private void OnOpenUriFailed(Uri uri) => Dispatcher.UIThread.Post(() => ShowAsync(uri).FireAndForget(logger));

    // The dialog's Copy button copies the markdown contents verbatim, so they hold the bare link.
    internal Task ShowAsync(Uri uri) => MessageBoxOkViewModel.Show(
        serviceProvider,
        title: "Could not open the link",
        description: "No browser could be started. Copy the link and open it yourself.",
        markdown: uri.AbsoluteUri
    );
}
