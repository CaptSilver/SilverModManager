using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Logging;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.CrossPlatform;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Settings;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.App.UI.Windows;

/// <summary>
/// The one place a failure becomes something the user reads. A refusal (<see cref="UserFacingException"/>) is
/// shown as its own sentence; anything else is a crash, shown as a short notice that points to the log. Neither
/// ever draws a stack trace.
/// </summary>
[PublicAPI]
public interface IErrorPresenter
{
    /// <summary>
    /// Presents a failure a catch site caught, after logging it with its exception. Completes when the user has
    /// dismissed the dialog (or at once when there is nothing to show or the failure was already presented).
    /// Never throws.
    /// </summary>
    /// <param name="exception">What was caught.</param>
    /// <param name="activity">What was being done, as it completes "Something went wrong while ...", for example "launching the game".</param>
    Task Report(Exception exception, string activity);

    /// <summary>
    /// Presents the errors that reach the log without a catch site, such as unobserved tasks and jobs nobody
    /// awaited. Connects once; later calls are ignored, since the window that asks can activate many times.
    /// </summary>
    void Connect(IObservable<LogMessage> errors);
}

/// <inheritdoc />
public sealed class ErrorPresenter(IServiceProvider serviceProvider, ILogger<ErrorPresenter> logger) : IErrorPresenter
{
    /// <summary>
    /// The second button beside OK on a refusal that carries <see cref="OpenHealthCheck"/>. The Health Check holds
    /// the diagnostic with the choices for fixing the refusal, so the dialog points there instead of repeating them.
    /// </summary>
    internal static readonly ButtonDefinitionId OpenHealthCheckButton = ButtonDefinitionId.From("openHealthCheck");

    internal static readonly ButtonDefinitionId OpenLogFolderButton = ButtonDefinitionId.From("openLogFolder");

    private const string CrashTitle = "Something went wrong";

    // Every exception of a failure that was presented, the wrapper and each exception inside it: the same failure
    // reaches the presenter from the catch site and again from the log, usually wrapped differently.
    private readonly ConditionalWeakTable<Exception, object> _presented = new();
    private readonly object _gate = new();
    private bool _connected;

    // Only touched on the UI thread.
    private OpenCrash? _openCrash;

    /// <inheritdoc />
    public Task Report(Exception exception, string activity)
    {
        // Marked before the log line: the log feeds the same instance back through Connect, and the mark is
        // what makes that second sighting a no-op.
        if (!FirstSighting(exception)) return Task.CompletedTask;

        logger.Log(UserFacingErrors.Find(exception) is null ? LogLevel.Error : LogLevel.Warning, exception, "Failed while {Activity}", activity);
        return Present(exception, $"Something went wrong while {activity}.");
    }

    /// <inheritdoc />
    public void Connect(IObservable<LogMessage> errors)
    {
        lock (_gate)
        {
            if (_connected) return;
            _connected = true;
        }

        errors.Subscribe(message =>
        {
            if (message.Exception is { } exception && !FirstSighting(exception)) return;
            _ = Present(message.Exception, message.Message);
        });
    }

    private async Task Present(Exception? exception, string headline)
    {
        try
        {
            var refusal = UserFacingErrors.Find(exception);
            await DispatcherHelper.EnsureOnUIThreadAsync(() => refusal is null ? ShowCrash(exception, headline) : ShowRefusal(refusal));
        }
        catch (Exception e)
        {
            // Logged below Error: an Error would come straight back through Connect and fail the same way.
            logger.LogWarning(e, "Could not show an error dialog");
        }
    }

    private async Task ShowRefusal(UserFacingException refusal)
    {
        var markdown = serviceProvider.GetRequiredService<IMarkdownRendererViewModel>();
        markdown.Contents = refusal.Message;

        var buttons = new List<DialogButtonDefinition> { new("OK", ButtonDefinitionId.Accept, ButtonAction.Accept) };
        switch (refusal.Action)
        {
            case null:
                break;
            case OpenHealthCheck:
                buttons.Add(new DialogButtonDefinition("Open Health Check", OpenHealthCheckButton, ButtonAction.Accept, ButtonStyling.Primary));
                break;
            default:
                throw new NotSupportedException($"No button is defined for the refusal action {refusal.Action.GetType().Name}.");
        }

        var dialog = DialogFactory.CreateStandardDialog(refusal.Title, new StandardDialogParameters { Markdown = markdown }, buttons.ToArray());
        var windowManager = serviceProvider.GetRequiredService<IWindowManager>();
        var result = await windowManager.ShowDialog(dialog, DialogWindowType.Modal);
        if (result.ButtonId == OpenHealthCheckButton && refusal.Action is OpenHealthCheck open) NavigateToHealthCheck(windowManager, open.LoadoutId);
    }

    private async Task ShowCrash(Exception? exception, string headline)
    {
        // The log has the whole story either way; the switch only decides whether the user is interrupted.
        if (!serviceProvider.GetRequiredService<ISettingsManager>().Get<LoggingSettings>().ShowExceptions) return;

        // One crash per mod in a bad collection would stack a dialog per mod, so while one is open the rest
        // only raise its counter.
        if (_openCrash is { } open)
        {
            open.More++;
            return;
        }

        var markdown = serviceProvider.GetRequiredService<IMarkdownRendererViewModel>();
        var crash = new OpenCrash(headline, markdown);
        _openCrash = crash;
        try
        {
            var report = exception is null ? headline : $"{headline}{Environment.NewLine}{Environment.NewLine}{exception}";
            var dialog = DialogFactory.CreateStandardDialog(
                CrashTitle,
                new StandardDialogParameters { Markdown = markdown, ShowMarkdownCopyButton = true, CopyText = report },
                [
                    new DialogButtonDefinition("OK", ButtonDefinitionId.Accept, ButtonAction.Accept),
                    new DialogButtonDefinition("Open log folder", OpenLogFolderButton),
                ]
            );

            var result = await serviceProvider.GetRequiredService<IWindowManager>().ShowDialog(dialog, DialogWindowType.Modal);
            if (result.ButtonId == OpenLogFolderButton)
            {
                var fileSystem = serviceProvider.GetRequiredService<IFileSystem>();
                serviceProvider.GetRequiredService<IOSInterop>().OpenDirectory(LoggingSettings.GetLogBaseFolder(fileSystem));
            }
        }
        finally
        {
            _openCrash = null;
        }
    }

    private static void NavigateToHealthCheck(IWindowManager windowManager, LoadoutId loadoutId)
    {
        var pageData = new PageData
        {
            FactoryId = DiagnosticListPageFactory.StaticId,
            Context = new DiagnosticListPageContext { LoadoutId = loadoutId },
        };

        // The loadout's workspace may not be the one in front (the dialog is also offered from the loadout cards).
        // A workspace created here already opens the page, so it is opened the way the left menu's Health Check
        // item opens it; a forced new tab would show the Health Check twice.
        var controller = windowManager.ActiveWorkspaceController;
        var workspace = controller.ChangeOrCreateWorkspaceByContext<LoadoutContext>(
            context => context.LoadoutId == loadoutId,
            () => pageData,
            () => new LoadoutContext { LoadoutId = loadoutId }
        );
        var behavior = controller.GetOpenPageBehavior(pageData, NavigationInformation.From(NavigationInput.Default));
        controller.OpenPage(workspace.Id, pageData, behavior);
    }

    /// <summary>
    /// Marks the exception and everything inside it as presented. False when any of them already was.
    /// </summary>
    private bool FirstSighting(Exception exception)
    {
        var all = new List<Exception>();
        Flatten(exception, all);

        lock (_gate)
        {
            if (all.Any(e => _presented.TryGetValue(e, out _))) return false;
            foreach (var e in all) _presented.Add(e, this);
            return true;
        }
    }

    private static void Flatten(Exception exception, List<Exception> into)
    {
        into.Add(exception);
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions) Flatten(inner, into);
        }
        else if (exception.InnerException is { } innerException)
        {
            Flatten(innerException, into);
        }
    }

    /// <summary>
    /// The crash dialog on screen, whose text says how many further crashes arrived while it was open.
    /// </summary>
    private sealed class OpenCrash
    {
        private readonly string _headline;
        private readonly IMarkdownRendererViewModel _markdown;
        private int _more;

        public OpenCrash(string headline, IMarkdownRendererViewModel markdown)
        {
            _headline = headline;
            _markdown = markdown;
            Refresh();
        }

        public int More
        {
            get => _more;
            set
            {
                _more = value;
                Refresh();
            }
        }

        private void Refresh() => _markdown.Contents = Text;

        private string Text => _more == 0
            ? $"{_headline}\n\nThe details were written to the log."
            : $"{_headline}\n\nThe details were written to the log. {_more} more {(_more == 1 ? "error" : "errors")} happened since, see the log for them.";
    }
}
