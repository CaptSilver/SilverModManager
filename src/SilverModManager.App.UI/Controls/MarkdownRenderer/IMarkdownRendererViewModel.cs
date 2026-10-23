using System.Reactive;
using JetBrains.Annotations;
using Markdown.Avalonia.Plugins;
using Markdown.Avalonia.Utils;
using NexusMods.UI.Sdk;
using ReactiveUI;

namespace NexusMods.App.UI.Controls.MarkdownRenderer;

public interface IMarkdownRendererViewModel : IViewModelInterface
{
    /// <summary>
    /// Gets or sets the contents of the renderer.
    /// </summary>
    [LanguageInjection(injectedLanguage: "markdown")]
    public string Contents { get; set; }

    public IMdAvPlugin ImageResolverPlugin { get; }
    public IPathResolver PathResolver { get; }
    public ReactiveCommand<string, Unit> OpenLinkCommand { get; }

    /// <summary>
    /// Copies the given text, the exact contents of a code block, to the clipboard.
    /// </summary>
    public ReactiveCommand<string, Unit> CopyCodeCommand { get; }
}
