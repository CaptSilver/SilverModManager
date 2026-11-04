using System.Reactive;
using JetBrains.Annotations;
using Markdown.Avalonia.Plugins;
using Markdown.Avalonia.Utils;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.App.UI.Controls.MarkdownRenderer;

public interface IMarkdownRendererViewModel : IViewModelInterface
{
    /// <summary>
    /// Gets or sets the contents of the renderer.
    /// </summary>
    [LanguageInjection(injectedLanguage: "markdown")]
    public string Contents { get; set; }

    /// <summary>
    /// Whether <c>https</c> images in the contents are fetched. Dialogs and message boxes turn it off: they show
    /// refusals and prompts that carry names from mod pages and collections, and a fetched image tells its host that
    /// the dialog was opened.
    /// </summary>
    public bool AllowRemoteImages { get; set; }

    public IMdAvPlugin ImageResolverPlugin { get; }
    public IPathResolver PathResolver { get; }
    public ReactiveCommand<string, Unit> OpenLinkCommand { get; }

    /// <summary>
    /// Copies the given text, the exact contents of a code block, to the clipboard.
    /// </summary>
    public ReactiveCommand<string, Unit> CopyCodeCommand { get; }
}
