namespace SilverModManager.UI.Tests.Pages.Collections;

/// <summary>A service provider that hands out one given instance for one service type and defers to <paramref name="inner"/> for the rest.</summary>
internal sealed class OverridingServices(IServiceProvider inner, Type serviceType, object instance) : IServiceProvider
{
    public object? GetService(Type requested) => requested == serviceType ? instance : inner.GetService(requested);
}
