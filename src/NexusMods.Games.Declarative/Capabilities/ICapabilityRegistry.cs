using System.Diagnostics.CodeAnalysis;

namespace NexusMods.Games.Declarative.Capabilities;

/// <summary>The capabilities a definition can name.</summary>
public interface ICapabilityRegistry
{
    IReadOnlyCollection<string> Names { get; }

    bool TryGet(string name, [NotNullWhen(true)] out IGameCapability? capability);
}

public sealed class CapabilityRegistry : ICapabilityRegistry
{
    private readonly Dictionary<string, IGameCapability> _capabilities = new(StringComparer.Ordinal);

    /// <exception cref="ArgumentException">Two capabilities share a name.</exception>
    public CapabilityRegistry(IEnumerable<IGameCapability> capabilities)
    {
        foreach (var capability in capabilities)
        {
            if (!_capabilities.TryAdd(capability.Name, capability))
                throw new ArgumentException($"Capability '{capability.Name}' is registered twice: {_capabilities[capability.Name].GetType()} and {capability.GetType()}", nameof(capabilities));
        }
    }

    public IReadOnlyCollection<string> Names => _capabilities.Keys;

    public bool TryGet(string name, [NotNullWhen(true)] out IGameCapability? capability) => _capabilities.TryGetValue(name, out capability);
}
