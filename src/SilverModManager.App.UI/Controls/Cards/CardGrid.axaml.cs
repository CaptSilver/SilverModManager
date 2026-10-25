using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace SilverModManager.App.UI.Controls.Cards;

/// <summary>
/// A scrolling grid of cards, one per item, laid out as a virtualized list of lines (see <see cref="CardGridLayout"/>).
/// The page supplies the card through <see cref="CardTemplate"/> (a root <c>Border</c> with the <c>GridCard</c> class)
/// and the thumbnail loader its <see cref="CardThumbnail"/>s use; <see cref="EndReached"/> asks for the next page.
/// </summary>
public partial class CardGrid : UserControl
{
    /// <summary>How close to the end of the content, in pixels, the view has to scroll before <see cref="EndReached"/> fires.</summary>
    public const double NearEndDistance = 600;

    public static readonly StyledProperty<IDataTemplate?> CardTemplateProperty = AvaloniaProperty.Register<CardGrid, IDataTemplate?>(nameof(CardTemplate));
    public static readonly StyledProperty<IThumbnailLoader?> ThumbnailsProperty = AvaloniaProperty.Register<CardGrid, IThumbnailLoader?>(nameof(Thumbnails));
    public static readonly StyledProperty<IReadOnlyList<object>?> ItemsProperty = AvaloniaProperty.Register<CardGrid, IReadOnlyList<object>?>(nameof(Items));

    private IReadOnlyList<object> _items = [];
    private ObservableCollection<CardLine> _lines = [];
    private int _columns;

    static CardGrid()
    {
        ItemsProperty.Changed.AddClassHandler<CardGrid>((grid, e) => grid.SetItems(e.NewValue as IReadOnlyList<object> ?? []));
    }

    public CardGrid()
    {
        InitializeComponent();
        Lines.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
    }

    public IDataTemplate? CardTemplate
    {
        get => GetValue(CardTemplateProperty);
        set => SetValue(CardTemplateProperty, value);
    }

    /// <summary>Supplies the images of the <see cref="CardThumbnail"/>s inside the cards.</summary>
    public IThumbnailLoader? Thumbnails
    {
        get => GetValue(ThumbnailsProperty);
        set => SetValue(ThumbnailsProperty, value);
    }

    /// <summary>The items to show a card for. A list that continues the previous one keeps the scroll position.</summary>
    public IReadOnlyList<object>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    /// <summary>Raised when the view is scrolled near the end of its content, or when the content doesn't fill the view.</summary>
    public event EventHandler? EndReached;

    // The scrollbar takes its share of the list's width.
    private double ListWidth() => Lines.Bounds.Width - 24;

    private void OnListSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (CardGridLayout.ColumnsFor(ListWidth()) != _columns) SetItems(_items, forceReset: true);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // Nothing to scroll yet: the empty view is as tall as its viewport, which would read as "needs more".
        if (e.Source is not ScrollViewer scroll || _items.Count == 0) return;
        if (scroll.Extent.Height - (scroll.Offset.Y + scroll.Viewport.Height) <= NearEndDistance) EndReached?.Invoke(this, EventArgs.Empty);
    }

    private void SetItems(IReadOnlyList<object> items, bool forceReset = false)
    {
        var columns = CardGridLayout.ColumnsFor(ListWidth());
        if (!forceReset && columns == _columns && Continues(_items, items))
        {
            Append(items, columns);
        }
        else
        {
            _columns = columns;
            _lines = new ObservableCollection<CardLine>(CardGridLayout.Chunk(items, columns));
            Lines.ItemsSource = _lines;
            // A different list (a new search) is not a continuation: the old offset means nothing in it.
            if (!forceReset) Lines.Scroll?.Offset = default;
        }
        _items = items;
    }

    // Appending to the lines already shown instead of replacing them is what keeps a page loaded near the end from
    // throwing the user back to the top.
    private void Append(IReadOnlyList<object> items, int columns)
    {
        var firstChanged = _items.Count / columns;
        if (firstChanged < _lines.Count) _lines.RemoveAt(firstChanged);
        foreach (var line in CardGridLayout.Chunk(items, columns, startAt: firstChanged * columns)) _lines.Add(line);
    }

    private static bool Continues(IReadOnlyList<object> before, IReadOnlyList<object> after)
    {
        if (before.Count == 0 || after.Count < before.Count) return false;
        for (var i = 0; i < before.Count; i++)
        {
            if (!ReferenceEquals(before[i], after[i])) return false;
        }
        return true;
    }
}
