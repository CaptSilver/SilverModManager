using Microsoft.Extensions.Logging;
using NSubstitute;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Windows;
using SilverModManager.CrossPlatform;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.UI.Tests.Helpers;

/// <summary>
/// Services that fall back to <paramref name="inner"/> for everything not overridden.
/// </summary>
internal sealed class OverridingServiceProvider(IServiceProvider inner, params (Type Service, object Instance)[] overrides) : IServiceProvider
{
    public object? GetService(Type serviceType) =>
        overrides.Where(o => o.Service == serviceType).Select(o => o.Instance).FirstOrDefault() ?? inner.GetService(serviceType);
}

/// <summary>
/// Collects what a logger was asked to write.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, Exception? Exception)> _entries = [];

    public (LogLevel Level, Exception? Exception)[] Entries
    {
        get { lock (_entries) return _entries.ToArray(); }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries) _entries.Add((logLevel, exception));
    }
}

/// <summary>
/// An <see cref="ErrorPresenter"/> over a window manager that records the dialogs instead of opening them, and
/// an OS interop that opens nothing.
/// </summary>
internal sealed class PresenterRig
{
    private readonly object _gate = new();
    private readonly List<DialogViewModel> _dialogs = [];

    /// <summary>The answer each dialog gets, unless the test replaces it (for example with a task it completes later).</summary>
    public Func<DialogViewModel, Task<StandardDialogResult>> Answer { get; set; } =
        _ => Task.FromResult(new StandardDialogResult { ButtonId = ButtonDefinitionId.Accept });

    public IWindowManager Windows { get; } = Substitute.For<IWindowManager>();
    public IOSInterop Os { get; } = Substitute.For<IOSInterop>();
    public RecordingLogger<ErrorPresenter> Log { get; } = new();
    public ErrorPresenter Presenter { get; }
    public IServiceProvider Services { get; }

    /// <summary>Whether every dialog was opened from the UI thread.</summary>
    public bool AllOnUiThread { get; private set; } = true;

    public DialogViewModel[] Dialogs
    {
        get { lock (_gate) return _dialogs.ToArray(); }
    }

    public PresenterRig(IServiceProvider provider, bool showExceptions = true)
    {
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<LoggingSettings>().Returns(provider.GetService(typeof(ISettingsManager)) is ISettingsManager real
            ? real.Get<LoggingSettings>() with { ShowExceptions = showExceptions }
            : new LoggingSettings { ShowExceptions = showExceptions });

        Windows.ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>()).Returns(call =>
        {
            var viewModel = ((Dialog)call.Arg<IDialog>()).ViewModel;
            lock (_gate)
            {
                _dialogs.Add(viewModel);
                AllOnUiThread &= Avalonia.Threading.Dispatcher.UIThread.CheckAccess();
            }
            return Answer(viewModel);
        });

        Services = new OverridingServiceProvider(provider, (typeof(IWindowManager), Windows), (typeof(IOSInterop), Os), (typeof(ISettingsManager), settings));
        Presenter = new ErrorPresenter(Services, Log);
        Services = new OverridingServiceProvider(Services, (typeof(IErrorPresenter), Presenter));
    }

    public static IDialogStandardContentViewModel Content(DialogViewModel dialog) => (IDialogStandardContentViewModel)dialog.ContentViewModel!;

    public static string Body(DialogViewModel dialog) => Content(dialog).MarkdownRenderer!.Contents;
}
