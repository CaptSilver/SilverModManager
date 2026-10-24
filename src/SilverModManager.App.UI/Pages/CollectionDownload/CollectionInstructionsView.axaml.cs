using Avalonia;
using Avalonia.Controls;

namespace SilverModManager.App.UI.Pages.CollectionDownload;

/// <summary>
/// The collection author's instructions, shared by the download page and the installed collection's page.
/// </summary>
public partial class CollectionInstructionsView : UserControl
{
    public static readonly StyledProperty<CollectionInstructions> InstructionsProperty =
        AvaloniaProperty.Register<CollectionInstructionsView, CollectionInstructions>(nameof(Instructions), CollectionInstructions.None);

    public CollectionInstructions Instructions
    {
        get => GetValue(InstructionsProperty);
        set => SetValue(InstructionsProperty, value);
    }

    public CollectionInstructionsView()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != InstructionsProperty) return;

        var instructions = Instructions;

        CollectionInstructionsExpander.IsVisible = instructions.Collection is not null;
        CollectionInstructionsRendererHost.ViewModel = instructions.Collection;

        RequiredModsInstructionsExpander.IsVisible = instructions.RequiredMods.Length > 0;
        RequiredModsInstructions.ItemsSource = instructions.RequiredMods;

        OptionalModsInstructionsExpander.IsVisible = instructions.OptionalMods.Length > 0;
        OptionalModsInstructions.ItemsSource = instructions.OptionalMods;
    }
}
