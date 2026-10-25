using System.Reactive.Disposables;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace SilverModManager.App.UI.Controls.Cards;

/// <summary>
/// The sort drop-down every store page shows above its cards. It lists <see cref="Options"/> by label and exposes
/// the chosen one's key as <see cref="SelectedKey"/>, so a page binds a string and the box stays free of any one
/// store's sort enum.
/// </summary>
public class StoreSortBox : ComboBox
{
    public static readonly StyledProperty<IReadOnlyList<StoreSortOption>?> OptionsProperty =
        AvaloniaProperty.Register<StoreSortBox, IReadOnlyList<StoreSortOption>?>(nameof(Options));

    public static readonly StyledProperty<string?> SelectedKeyProperty =
        AvaloniaProperty.Register<StoreSortBox, string?>(nameof(SelectedKey), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    static StoreSortBox()
    {
        OptionsProperty.Changed.AddClassHandler<StoreSortBox>((box, _) => box.ShowOptions());
        SelectedKeyProperty.Changed.AddClassHandler<StoreSortBox>((box, _) => box.SelectOptionOfKey());
        // Clearing the items clears the selection too; that is not the user choosing nothing, so only an option counts.
        SelectedItemProperty.Changed.AddClassHandler<StoreSortBox>((box, e) =>
        {
            if (e.NewValue is StoreSortOption option) box.SelectedKey = option.Key;
        });
    }

    public StoreSortBox()
    {
        ItemTemplate = new FuncDataTemplate<StoreSortOption>((option, _) => new TextBlock { Text = option?.Label });
    }

    // A ComboBox with its own look-less subclass would otherwise find no template for the new type.
    protected override Type StyleKeyOverride => typeof(ComboBox);

    public IReadOnlyList<StoreSortOption>? Options
    {
        get => GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    /// <summary>The key of the chosen option; a key no option has leaves the box empty.</summary>
    public string? SelectedKey
    {
        get => GetValue(SelectedKeyProperty);
        set => SetValue(SelectedKeyProperty, value);
    }

    private void ShowOptions()
    {
        ItemsSource = Options;
        SelectOptionOfKey();
    }

    private void SelectOptionOfKey()
    {
        var option = Options?.FirstOrDefault(o => o.Key == SelectedKey);
        if (!ReferenceEquals(option, SelectedItem)) SelectedItem = option;
    }
}

public static class StoreSortBoxExtensions
{
    /// <summary>
    /// Keeps the box and a page's sort enum in step both ways: the box shows <paramref name="current"/> as it
    /// changes (a restored choice included), and a pick in the box goes to <paramref name="choose"/>. The options'
    /// keys are the enum's member names.
    /// </summary>
    public static IDisposable BindSort<TSort>(this StoreSortBox box, IObservable<TSort> current, Action<TSort> choose) where TSort : struct, Enum
    {
        return new CompositeDisposable(
            current.Subscribe(sort => box.SelectedKey = sort.ToString()),
            box.GetObservable(StoreSortBox.SelectedKeyProperty).Subscribe(key =>
            {
                if (Enum.TryParse<TSort>(key, out var sort) && Enum.IsDefined(sort)) choose(sort);
            }));
    }
}
