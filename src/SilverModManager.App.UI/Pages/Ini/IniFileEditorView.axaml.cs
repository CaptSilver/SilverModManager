using System.Reactive.Disposables;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using AvaloniaEdit;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Resources;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Ini;

/// <summary>The four tabs of one INI file: raw copy, tweaks, settings and result.</summary>
public partial class IniFileEditorView : UserControl
{
    private readonly SerialDisposable _binding = new();

    public IniFileEditorView()
    {
        InitializeComponent();

        RawTab.Header = Language.IniPage_Tab_Raw;
        TweaksTab.Header = Language.IniPage_Tab_Tweaks;
        SettingsTab.Header = Language.IniPage_Tab_Settings;
        ResultTab.Header = Language.IniPage_Tab_Result;
        RawHintText.Text = Language.IniPage_RawHint;
        TweaksHintText.Text = Language.IniPage_TweaksHint;
        LoadoutTweakHeading.Text = Language.IniPage_LoadoutTweak;
        SettingsHintText.Text = Language.IniPage_SettingsHint;
        ResultHintText.Text = Language.IniPage_ResultHint;
        SaveBaselineButton.Text = Language.IniPage_Save;
        ResetFromDiskButton.Text = Language.IniPage_ResetBaselineFromDisk;
        SaveTweakButton.Text = Language.IniPage_Save;
        ResetToBaselineButton.Text = Language.IniPage_ResetToBaseline;

        DataContextChanged += (_, _) => _binding.Disposable = DataContext is IniFileEditor editor ? Bind(editor) : Disposable.Empty;
    }

    private IDisposable Bind(IniFileEditor editor)
    {
        var disposables = new CompositeDisposable();

        SaveBaselineButton.Command = editor.SaveBaselineCommand;
        ResetFromDiskButton.Command = editor.ResetBaselineFromDiskCommand;
        SaveTweakButton.Command = editor.SaveLoadoutTweakCommand;
        ResetToBaselineButton.Command = editor.ResetToBaselineCommand;

        // The editors hold text the view model owns: text goes in when the view model changes it, and back out
        // when the user types, which is told apart by comparing, so neither direction echoes.
        Link(RawEditor, editor, e => e.BaselineText, (e, text) => e.BaselineText = text, disposables);
        Link(TweakEditor, editor, e => e.LoadoutTweakText, (e, text) => e.LoadoutTweakText = text, disposables);

        editor.WhenAnyValue(e => e.ResultText).Subscribe(text => ResultEditor.Text = text).DisposeWith(disposables);
        editor.WhenAnyValue(e => e.Diff)
            .Subscribe(diff => DiffEditor.Text = string.Join(Environment.NewLine, diff.Select(Prefixed)))
            .DisposeWith(disposables);
        editor.WhenAnyValue(e => e.HasBaseline)
            .Subscribe(has =>
            {
                NoBaselineText.Text = has ? "" : string.Format(Language.IniPage_NoBaseline, editor.FileName);
                NoBaselineText.IsVisible = !has;
            })
            .DisposeWith(disposables);

        editor.WhenAnyValue(e => e.Layers).Subscribe(layers => ShowLayers(editor, layers)).DisposeWith(disposables);
        editor.WhenAnyValue(e => e.Settings).Subscribe(settings => ShowSettings(settings)).DisposeWith(disposables);

        return disposables;
    }

    private static void Link(TextEditor box, IniFileEditor editor, System.Linq.Expressions.Expression<Func<IniFileEditor, string>> property, Action<IniFileEditor, string> write, CompositeDisposable disposables)
    {
        var read = property.Compile();
        editor.WhenAnyValue(property).Subscribe(text =>
        {
            if (box.Text != text) box.Text = text;
        }).DisposeWith(disposables);

        void OnChanged(object? sender, EventArgs args)
        {
            if (box.Text != read(editor)) write(editor, box.Text);
        }

        box.TextChanged += OnChanged;
        disposables.Add(Disposable.Create(() => box.TextChanged -= OnChanged));
    }

    private static string Prefixed(IniDiffLine line) => line.Kind switch
    {
        IniDiffKind.Added => "+ " + line.Text,
        IniDiffKind.Removed => "- " + line.Text,
        _ => "  " + line.Text,
    };

    private void ShowLayers(IniFileEditor editor, IReadOnlyList<IniLayerEntry> layers)
    {
        LayersPanel.Children.Clear();
        // The loadout's own tweak is edited below, not switched.
        foreach (var layer in layers.Where(l => !l.IsLoadoutTweak))
        {
            var toggle = new ToggleSwitch
            {
                IsChecked = layer.IsOn,
                Content = $"{layer.Name}  {string.Format(Language.IniPage_LayerSource, layer.SourceName)}{(layer.IsOn && !layer.IsActive ? "  (" + Language.IniPage_LayerModOff + ")" : "")}",
            };
            toggle.IsCheckedChanged += (_, _) =>
            {
                if (toggle.IsChecked != layer.IsOn) editor.ToggleLayerCommand.Execute(layer).Subscribe();
            };
            LayersPanel.Children.Add(toggle);
        }
    }

    private void ShowSettings(IReadOnlyList<IniSettingEntry> settings)
    {
        SettingsPanel.Children.Clear();
        foreach (var setting in settings)
        {
            var value = new TextBox { Text = setting.Value, MinWidth = 220, IsEnabled = !setting.IsManaged };
            value.TextChanged += (_, _) => setting.Value = value.Text ?? "";

            var apply = new StandardButton { Text = Language.IniPage_Apply, Type = StandardButton.Types.Secondary, Size = StandardButton.Sizes.Toolbar, Command = setting.ApplyCommand };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = $"{setting.Definition.Label}  [{setting.Definition.Section}] {setting.Definition.Key}", Width = 320, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(value);
            row.Children.Add(apply);

            var block = new StackPanel { Spacing = 2 };
            block.Children.Add(row);
            if (setting.IsManaged)
                block.Children.Add(new TextBlock { Text = $"{Language.IniPage_Managed}. {setting.ManagedReason}", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            SettingsPanel.Children.Add(block);
        }
    }
}
