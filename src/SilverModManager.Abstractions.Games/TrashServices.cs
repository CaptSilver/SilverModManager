using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Paths;

namespace SilverModManager.Abstractions.Games;

public static class TrashServices
{
    /// <summary>The user's home trash under <c>$XDG_DATA_HOME</c>, for every host that builds games or runs their fixes.</summary>
    public static IServiceCollection AddFreedesktopTrash(this IServiceCollection services)
    {
        services.TryAddSingleton<ITrash>(provider => new FreedesktopTrash(provider.GetRequiredService<IFileSystem>().GetKnownPath(KnownPath.XDG_DATA_HOME).GetFullPath()));
        return services;
    }
}
