using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.Collections;

namespace SilverModManager.App.UI.Pages.CollectionDownload;

/// <summary>
/// What a collection's author wrote for the person installing it: a text for the whole collection and notes on single mods.
/// </summary>
public record CollectionInstructions(
    IMarkdownRendererViewModel? Collection,
    ModInstructions[] RequiredMods,
    ModInstructions[] OptionalMods)
{
    public static CollectionInstructions None { get; } = new(null, [], []);

    public bool HasAny => Collection is not null || RequiredMods.Length > 0 || OptionalMods.Length > 0;

    /// <inheritdoc cref="From(CollectionRoot, Func{string, IMarkdownRendererViewModel})"/>
    public static CollectionInstructions From(CollectionRoot collectionRoot, IServiceProvider serviceProvider)
    {
        return From(collectionRoot, text =>
        {
            var renderer = serviceProvider.GetRequiredService<IMarkdownRendererViewModel>();
            renderer.Contents = text;
            return renderer;
        });
    }

    /// <summary>
    /// The instructions in a parsed collection.json. Blank texts count as none, so a collection without any gets no instructions tab.
    /// </summary>
    public static CollectionInstructions From(CollectionRoot collectionRoot, Func<string, IMarkdownRendererViewModel> createRenderer)
    {
        var modsInstructions = collectionRoot.Mods
            .Where(static mod => !string.IsNullOrWhiteSpace(mod.Instructions))
            .Select(static mod => new ModInstructions(mod.Name, mod.Instructions, mod.Optional ? CollectionDownloader.ItemType.Optional : CollectionDownloader.ItemType.Required))
            .ToArray();

        var collectionText = collectionRoot.Info.InstallInstructions;

        return new CollectionInstructions(
            Collection: string.IsNullOrWhiteSpace(collectionText) ? null : createRenderer(collectionText),
            RequiredMods: modsInstructions.Where(static x => x.ItemType == CollectionDownloader.ItemType.Required).ToArray(),
            OptionalMods: modsInstructions.Where(static x => x.ItemType == CollectionDownloader.ItemType.Optional).ToArray()
        );
    }
}
