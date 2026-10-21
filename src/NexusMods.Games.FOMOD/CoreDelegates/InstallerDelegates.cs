using FomodInstaller.Interface;
using FomodInstaller.Interface.ui;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.GuidedInstallers;

namespace NexusMods.Games.FOMOD.CoreDelegates;

[UsedImplicitly]
public sealed class InstallerDelegates : ICoreDelegates
{
    public IContextDelegates context { get; }
    public IIniDelegates ini => throw new NotImplementedException();
    public IPluginDelegates plugin { get; }

    public IUIDelegates ui => UiDelegates;
    public UiDelegates UiDelegates { get; }

    public InstallerDelegates(
        ILoggerFactory loggerFactory,
        IGuidedInstaller guidedInstaller)
    {
        context = new ContextDelegates(loggerFactory.CreateLogger<ContextDelegates>());
        plugin = new PluginDelegates(loggerFactory.CreateLogger<PluginDelegates>());
        UiDelegates = new UiDelegates(
            loggerFactory.CreateLogger<UiDelegates>(),
            guidedInstaller
        );
    }

    private InstallerDelegates(InstallerDelegates other, UiDelegates uiDelegates)
    {
        context = other.context;
        plugin = other.plugin;
        UiDelegates = uiDelegates;
    }

    /// <summary>
    /// Creates delegates for a single execution that share the context and plugin delegates but use
    /// <paramref name="uiDelegates"/>. The FOMOD library reads the UI delegates on every callback, so
    /// executions running at the same time must not share one instance.
    /// </summary>
    public InstallerDelegates WithUiDelegates(UiDelegates uiDelegates) => new(this, uiDelegates);
}
