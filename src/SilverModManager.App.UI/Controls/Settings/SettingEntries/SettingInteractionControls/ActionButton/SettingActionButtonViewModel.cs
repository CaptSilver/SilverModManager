using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using R3;
using Unit = System.Reactive.Unit;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using SilverModManager.App.UI.Windows;
using SilverModManager.Sdk.Settings;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Settings;

namespace SilverModManager.App.UI.Controls.Settings.SettingEntries;

public partial class SettingActionButtonViewModel : AViewModel<ISettingActionButtonViewModel>, ISettingActionButtonViewModel
{
    private readonly IActionButtonSource _source;

    public ActionButtonContainer ActionContainer { get; }

    public IPropertyValueContainer ValueContainer => ActionContainer;

    [Reactive] public partial string Text { get; private set; }
    [Reactive] public partial string? ButtonLabel { get; private set; }
    [Reactive] public partial bool HasButton { get; private set; }

    public ReactiveUI.ReactiveCommand<Unit, Unit> InvokeCommand { get; }

    public SettingActionButtonViewModel(ActionButtonContainer container, IActionButtonSource source, IErrorPresenter errors)
    {
        ActionContainer = container;
        _source = source;
        Show(source.Current);

        InvokeCommand = ReactiveUI.ReactiveCommand.CreateFromTask(async cancellationToken =>
        {
            try
            {
                await source.Invoke(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                await errors.Report(e, "changing the account");
            }
        }, this.WhenAnyValue(vm => vm.HasButton));

        this.WhenActivated(disposables =>
        {
            // The status can change from another thread (a token written by a download or another process).
            Show(source.Current);
            source.State
                .AsSystemObservable()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(Show)
                .DisposeWith(disposables);
        });
    }

    private void Show(ActionButtonState state)
    {
        Text = state.Text;
        ButtonLabel = state.ButtonLabel;
        HasButton = state.ButtonLabel is not null;
    }
}

[UsedImplicitly]
public class SettingActionButtonFactory : IInteractionControlFactory<ActionButtonContainerOptions>
{
    public IInteractionControl Create(IServiceProvider serviceProvider, ISettingsManager settingsManager, ActionButtonContainerOptions containerOptions, PropertyConfig propertyConfig)
    {
        return new SettingActionButtonViewModel(
            new ActionButtonContainer(
                value: propertyConfig.GetValueCasted<string>(settingsManager),
                defaultValue: propertyConfig.GetDefaultValueCasted<string>(settingsManager),
                config: propertyConfig),
            containerOptions.CreateSource(serviceProvider),
            serviceProvider.GetRequiredService<IErrorPresenter>());
    }
}
