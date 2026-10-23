using JetBrains.Annotations;
using SilverModManager.App.UI.Controls;

namespace SilverModManager.App.UI.Pages.Sorting;


public static class LoadOrderColumns
{
    [UsedImplicitly]
    public sealed class IndexColumn : ICompositeColumnDefinition<IndexColumn>
    {
        public const string ColumnTemplateResourceKey = nameof(LoadOrderColumns) + "_" + nameof(IndexColumn);

        public static readonly ComponentKey IndexComponentKey = ComponentKey.From(ColumnTemplateResourceKey + "_" + nameof(SharedComponents.IndexComponent));

        public static int Compare<TKey>(CompositeItemModel<TKey> a, CompositeItemModel<TKey> b) where TKey : notnull
        {
            throw new NotImplementedException();
        }

        // The header name should be set on column creation as it is game dependent
        public static string GetColumnHeader() => throw new NotSupportedException();

        public static string GetColumnTemplateResourceKey() => ColumnTemplateResourceKey;

    }

    [UsedImplicitly]
    public sealed class DisplayNameColumn : ICompositeColumnDefinition<DisplayNameColumn>
    {
        public const string ColumnTemplateResourceKey = nameof(LoadOrderColumns) + "_" + nameof(DisplayNameColumn);

        public static readonly ComponentKey DisplayNameComponentKey = ComponentKey.From(ColumnTemplateResourceKey + "_" + "DisplayNameComponent");
        public static readonly ComponentKey ImageComponentKey = ComponentKey.From(ColumnTemplateResourceKey + "_" + nameof(ImageComponent));
        
        public static int Compare<TKey>(CompositeItemModel<TKey> a, CompositeItemModel<TKey> b) where TKey : notnull
        {
            throw new NotImplementedException();
        }

        // The header name should be set on column creation as it is game dependent
        public static string GetColumnHeader() => throw new NotSupportedException();

        public static string GetColumnTemplateResourceKey() => ColumnTemplateResourceKey;
    }
    
    [UsedImplicitly]
    public sealed class ModNameColumn : ICompositeColumnDefinition<ModNameColumn>
    {
        public const string ColumnTemplateResourceKey = nameof(LoadOrderColumns) + "_" + nameof(ModNameColumn);

        public static readonly ComponentKey ModNameComponentKey = ComponentKey.From(ColumnTemplateResourceKey + "_" + "ModNameComponent");
        
        public static int Compare<TKey>(CompositeItemModel<TKey> a, CompositeItemModel<TKey> b) where TKey : notnull
        {
            throw new NotImplementedException();
        }

        // The header name should be set on column creation as it is game dependent
        public static string GetColumnHeader() => "Parent mod";

        public static string GetColumnTemplateResourceKey() => ColumnTemplateResourceKey;
    }

    /// <summary>
    /// The on/off switch of a row, for varieties whose rows can be switched.
    /// </summary>
    [UsedImplicitly]
    public sealed class EnabledColumn : ICompositeColumnDefinition<EnabledColumn>
    {
        public const string ColumnTemplateResourceKey = nameof(LoadOrderColumns) + "_" + nameof(EnabledColumn);

        public static readonly ComponentKey EnabledComponentKey = ComponentKey.From(ColumnTemplateResourceKey + "_" + nameof(LoadOrderToggleComponent));

        public static int Compare<TKey>(CompositeItemModel<TKey> a, CompositeItemModel<TKey> b) where TKey : notnull
        {
            throw new NotImplementedException();
        }

        // The header is set on column creation, as it comes from the resources
        public static string GetColumnHeader() => throw new NotSupportedException();

        public static string GetColumnTemplateResourceKey() => ColumnTemplateResourceKey;
    }

    /// <summary>
    /// The flags and masters of a row, for rows that describe them.
    /// </summary>
    [UsedImplicitly]
    public sealed class DetailsColumn : ICompositeColumnDefinition<DetailsColumn>
    {
        public const string ColumnTemplateResourceKey = nameof(LoadOrderColumns) + "_" + nameof(DetailsColumn);

        public static readonly ComponentKey DetailsComponentKey = ComponentKey.From(ColumnTemplateResourceKey + "_" + nameof(LoadOrderDetailsComponent));

        public static int Compare<TKey>(CompositeItemModel<TKey> a, CompositeItemModel<TKey> b) where TKey : notnull
        {
            throw new NotImplementedException();
        }

        // The header is set on column creation, as it comes from the resources
        public static string GetColumnHeader() => throw new NotSupportedException();

        public static string GetColumnTemplateResourceKey() => ColumnTemplateResourceKey;
    }

    public static readonly ComponentKey IsActiveComponentKey = ComponentKey.From(nameof(LoadOrderColumns) + "_" + "IsActiveComponent");
    public static readonly string IsActiveStyleTag = "IsActive";
}
