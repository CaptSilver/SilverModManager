using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Tests.Framework;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests;

public class AUiTest : IAsyncDisposable
{
    private readonly List<Window> _windows = [];

    protected IServiceProvider Provider;

    protected IConnection Connection { get; }
    protected AvaloniaApp App { get; }

    public AUiTest(IServiceProvider provider)
    {
        Provider = provider;

        // Do this to trigger the AvaloniaApp constructor/initialization
        App = provider.GetRequiredService<AvaloniaApp>();
        Connection = provider.GetRequiredService<IConnection>();
    }

    /// <summary>
    /// Registers a window the test is about to show, so it is closed when the test ends. Windows left open keep
    /// the headless platform's keyboard focus and active window, which the next test's input would then reach.
    /// </summary>
    protected TWindow Track<TWindow>(TWindow window) where TWindow : Window
    {
        lock (_windows) _windows.Add(window);
        return window;
    }

    /// <summary>
    /// Shows a window and renders it. Pointer events find their target by hit testing the rendered scene, which
    /// headless only builds on a render tick, so input sent to a window that has not had one lands on nothing.
    /// Must run on the UI thread.
    /// </summary>
    protected static void ShowRendered(Window window)
    {
        window.Show();
        Render(window);
    }

    /// <summary>
    /// Lays the window out again and runs a render tick, for content that appears after the first pass (items
    /// controls realise their rows on a later job). Must run on the UI thread.
    /// </summary>
    protected static void Render(Window window)
    {
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    public virtual async ValueTask DisposeAsync()
    {
        Window[] windows;
        lock (_windows)
        {
            windows = _windows.ToArray();
            _windows.Clear();
        }
        if (windows.Length == 0) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var window in windows) window.Close();
        });
    }

    /// <summary>
    /// A loadout row on the test game's folder, recorded as installed from <paramref name="store"/>.
    /// Nothing is applied, since the test game has no files to apply.
    /// </summary>
    /// <remarks>
    /// The folder comes from the stub locator only: the app's own locators also run here and can find the
    /// machine's real copy of the game, which no test may attach a loadout to. A store the stub does not report
    /// gives a loadout whose install is not found, which is what those tests are about.
    /// </remarks>
    protected async Task<Loadout.ReadOnly> SeedLoadoutRow(GameStore store, string name)
    {
        var stubbed = Provider.GetRequiredService<IGameRegistry>().LocateGameInstallations()
            .Where(located => located.LocatorResult.Locator is UniversalStubbedGameLocator<Cyberpunk2077Game>)
            .ToArray();
        var installation = stubbed.FirstOrDefault(located => located.LocatorResult.Store == store) ?? stubbed.First();
        using var tx = Connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            Path = installation.LocatorResult.Path.ToString(),
            Name = installation.Game.DisplayName,
            Store = store,
            Game = installation.Game.GameId,
            GameId = installation.Game.NexusModsGameId.Value,
        };
        var loadout = new Loadout.New(tx)
        {
            Name = name,
            ShortName = "ZZZ",
            InstallationId = metadata,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };
        var result = await tx.Commit();
        return result.Remap(loadout);
    }

    /// <summary>Another loadout on the same installation as <paramref name="sibling"/>, as a second profile of one game is.</summary>
    protected async Task<Loadout.ReadOnly> SeedSiblingLoadoutRow(Loadout.ReadOnly sibling, string name)
    {
        using var tx = Connection.BeginTransaction();
        var loadout = new Loadout.New(tx)
        {
            Name = name,
            ShortName = "ZZY",
            InstallationId = sibling.InstallationId,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };
        var result = await tx.Commit();
        return result.Remap(loadout);
    }

    protected VMWrapper<T> GetActivatedViewModel<T>()
    where T : IViewModelInterface
    {
        var vm = Provider.GetRequiredService<T>();
        return new VMWrapper<T>(vm);
    }

    [SuppressMessage("ReSharper", "InconsistentNaming")]
    public class VMWrapper<T> : IDisposable where T : IViewModelInterface
    {
        private readonly IDisposable _disposable;
        public T VM { get; }
        public VMWrapper(T vm)
        {
            VM = vm;
            _disposable = vm.Activator.Activate();
        }

        public void Dispose()
        {
            _disposable.Dispose();
        }

        public void Deconstruct(out T vm)
        {
            vm = VM;
        }
    }


    /// <summary>
    /// Retries the action until it succeeds or the timeout is reached. If the timeout is reached, the last exception is
    /// rethrown.
    /// </summary>
    /// <param name="action">The code to run on each attempt</param>
    /// <param name="maxTimeout">The maximum amount of time to wait for a successful completion</param>
    /// <param name="delay">Amount of time to delay between each attempt</param>
    public async Task Eventually(Action action, TimeSpan? maxTimeout = null, TimeSpan? delay = null)
    {
        delay ??= TimeSpan.FromMilliseconds(500);
        maxTimeout ??= TimeSpan.FromSeconds(15);
        var sw = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                action();
                return;
            }
            catch (Exception)
            {
                if (sw.Elapsed >= maxTimeout)
                    throw;
                await Task.Delay(delay.Value).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Retries the action until it succeeds or the timeout is reached. If the timeout is reached, the last exception is
    /// rethrown.
    /// </summary>
    /// <param name="action">The code to run on each attempt</param>
    /// <param name="maxTimeout">The maximum amount of time to wait for a successful completion</param>
    /// <param name="delay">Amount of time to delay between each attempt</param>
    public async Task Eventually(Func<Task> action, TimeSpan? maxTimeout = null, TimeSpan? delay = null)
    {
        delay ??= TimeSpan.FromMilliseconds(500);
        maxTimeout ??= TimeSpan.FromSeconds(15);
        var sw = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception)
            {
                if (sw.Elapsed >= maxTimeout)
                    throw;
                await Task.Delay(delay.Value).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Like Eventually, but runs the action on the UI thread.
    /// </summary>
    /// <param name="action"></param>
    /// <param name="maxTimeout"></param>
    /// <param name="delay"></param>
    public async Task EventuallyOnUi(Func<Task> action, TimeSpan? maxTimeout = null, TimeSpan? delay = null)
    {
        await Eventually(async () =>
        {
            await OnUi(async () =>
            {
                await action();
            });
        });
    }

    /// <summary>
    /// Like Eventually, but runs the action on the UI thread.
    /// </summary>
    /// <param name="action"></param>
    /// <param name="maxTimeout"></param>
    /// <param name="delay"></param>
    public async Task EventuallyOnUi(Action action, TimeSpan? maxTimeout = null, TimeSpan? delay = null)
    {
        await Eventually(async () => await OnUi(action));
    }

    /// <summary>
    /// Executes an action on the UI thread and waits for it to complete.
    /// </summary>
    /// <param name="action"></param>
    protected async Task<T> OnUi<T>(Func<Task<T>> action)
    {
        var result = await Dispatcher.UIThread.InvokeAsync(action);
        return result;
    }

    /// <summary>
    /// Executes an action on the UI thread and waits for it to complete.
    /// </summary>
    /// <param name="action"></param>
    protected async Task OnUi(Func<Task> action)
    {
        var result = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await action();
            return 0;
        });
    }

    /// <summary>
    /// Executes an action on the UI thread and waits for it to complete.
    /// </summary>
    /// <param name="action"></param>
    protected async Task OnUi(Action action)
    {
        var result = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            action();
            return 0;
        });
    }

    /// <summary>
    /// Clicks the button in a way that fires all the proper UI events
    /// </summary>
    /// <param name="button"></param>
    protected async Task Click(Button button)
    {
        await OnUi(() => Click_AlreadyOnUi(button));
    }

    /// <summary>
    /// Clicks the button in a way that fires all the proper UI events, use this if you are
    /// already on UI via <see cref="OnUi{T}"/>.
    /// </summary>
    /// <param name="button"></param>
    protected void Click_AlreadyOnUi(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        peer.Invoke();
    }
}
