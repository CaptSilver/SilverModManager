using SilverModManager.Sdk;

namespace SilverModManager.Platform.Proton.Protontricks;

/// <summary>
/// Aggregates multiple Protontricks implementations, using either the native or Flatpak version
/// depending on availability.
/// </summary>
public class AggregateProtontricksDependency : AggregateExecutableRuntimeDependency
{
    /// <summary>
    /// Creates a new instance of the aggregate Protontricks dependency.
    /// </summary>
    /// <param name="processFactory">The process factory used to execute commands.</param>
    public AggregateProtontricksDependency(IProcessRunner runner) 
        : base(
            displayName: "Protontricks",
            description: "Manage Proton games and their dependencies",
            homepage: new Uri("https://github.com/Matoking/protontricks"),
            dependencies: new ExecutableRuntimeDependency[]
            {
                new ProtontricksNativeDependency(runner),
                new ProtontricksFlatpakDependency(runner),
            })
    {
    }

    /// <summary>
    /// The first Protontricks implementation that is installed, or null when there is none.
    /// </summary>
    public async ValueTask<IProtontricksDependency?> FindAvailable()
        => (await GetAvailableDependenciesAsync()).OfType<IProtontricksDependency>().FirstOrDefault();

    /// <summary>
    /// Gets whether any Protontricks implementation is available on the system.
    /// </summary>
    public async Task<bool> IsAvailable() => (await QueryInstallationInformation(CancellationToken.None)).HasValue;
}
