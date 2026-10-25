using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages.Diagnostics;

public record DiagnosticDetailsPageContext : IPageFactoryContext
{
    public required Diagnostic Diagnostic { get; init; }

    public required LoadoutId LoadoutId { get; init; }

    /// <inheritdoc/>
    public bool IsEphemeral => true;

    /// <inheritdoc/>
    public PageData GetSerializablePageData()
    {
        return new PageData
        {
            FactoryId = DiagnosticListPageFactory.StaticId,
            Context = new DiagnosticListPageContext
            {
                LoadoutId = LoadoutId,
            },
        };
    }
}

[UsedImplicitly]
public class DiagnosticDetailsPageFactory : APageFactory<IDiagnosticDetailsViewModel, DiagnosticDetailsPageContext>
{
    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("96A85EAB-3748-4D30-8212-7A09CCDA225B"));
    
    public override PageFactoryId Id => StaticId;
    
    public DiagnosticDetailsPageFactory(IServiceProvider serviceProvider) : base(serviceProvider) { }
    
    public override IDiagnosticDetailsViewModel CreateViewModel(DiagnosticDetailsPageContext context)
    {
        return new DiagnosticDetailsViewModel(
            WindowManager,
            ServiceProvider.GetRequiredService<IDiagnosticWriter>(),
            ServiceProvider.GetRequiredService<IMarkdownRendererViewModel>(),
            context.Diagnostic,
            context.LoadoutId,
            ServiceProvider.GetServices<IDiagnosticActionHandler>(),
            ServiceProvider.GetRequiredService<ILogger<DiagnosticDetailsViewModel>>(),
            ServiceProvider.GetRequiredService<IMarkdownRendererViewModel>()
        );
    }
}
