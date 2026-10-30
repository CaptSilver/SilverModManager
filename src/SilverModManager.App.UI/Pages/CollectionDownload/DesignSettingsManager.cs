using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using R3;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.App.UI.Pages.CollectionDownload;

/// <summary>
/// Settings for design-time view models that build a real tree adapter. The adapter only asks for the
/// manager when it saves or loads sorting state, which a design-time page never does.
/// </summary>
internal sealed class DesignSettingsManager : ISettingsManager
{
    private static NotSupportedException DesignTimeOnly() => new("design-time only: settings are not available");

    public void Set<T>(T value, string? key = null) where T : class, ISettings, new() => throw DesignTimeOnly();
    public T Get<T>(string? key = null) where T : class, ISettings, new() => throw DesignTimeOnly();
    public bool TryGet<T>([NotNullWhen(true)] out T? value, string? key = null) where T : class, ISettings, new() => throw DesignTimeOnly();
    public T GetDefault<T>() where T : class, ISettings, new() => throw DesignTimeOnly();
    public T Update<T>(Func<T, T> updater, string? key = null) where T : class, ISettings, new() => throw DesignTimeOnly();
    public Observable<T> GetChanges<T>(string? key = null, bool prependCurrent = false) where T : class, ISettings, new() => throw DesignTimeOnly();
    public FrozenDictionary<Type, SettingsConfig> Configs => throw DesignTimeOnly();
}
