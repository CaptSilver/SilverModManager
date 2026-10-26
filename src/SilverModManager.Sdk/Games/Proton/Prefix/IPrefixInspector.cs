using JetBrains.Annotations;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// What the prefix shows about one component.
/// </summary>
[PublicAPI]
public enum PrefixComponentState
{
    /// <summary>Native files and, where the component has one, its registry entry are there.</summary>
    Installed,

    /// <summary>Nothing but Wine's own placeholders, or nothing at all.</summary>
    Missing,

    /// <summary>Some evidence, not enough to say either way.</summary>
    Unknown,
}

/// <summary>
/// The result of checking one component.
/// </summary>
/// <param name="Id">The component checked.</param>
/// <param name="State">What was found.</param>
/// <param name="Evidence">One line saying why, for the user.</param>
[PublicAPI]
public sealed record PrefixComponentStatus(PrefixComponentId Id, PrefixComponentState State, string Evidence);

/// <summary>
/// Reads a prefix and reports which components are in it. Never writes.
/// </summary>
[PublicAPI]
public interface IPrefixInspector
{
    /// <summary>
    /// One status per component, in the order given. A DLL in <c>system32</c> is not evidence on its own, because
    /// Wine and Proton put placeholder DLLs there; a native file with a version resource is.
    /// </summary>
    /// <exception cref="ArgumentException">A component id is not in <see cref="PrefixComponents"/>.</exception>
    IReadOnlyList<PrefixComponentStatus> Inspect(ResolvedPrefix prefix, IReadOnlyList<PrefixComponentId> components);
}
