namespace NexusMods.Sdk;

/// <summary>
/// The names that identify this repository's own assemblies and solution. Tests that scan assemblies,
/// glob DLLs or look for the solution file read these instead of carrying their own copy of the prefix,
/// so a rename changes one place and a scan that silently matches nothing cannot hide behind a stale literal.
/// </summary>
/// <remarks>
/// The rename to the fork's namespace flips <see cref="AssemblyPrefix"/>, <see cref="AssemblyGlob"/> and
/// <see cref="SolutionFile"/>. <see cref="AppAssemblyName"/> never changes: the App project's assembly is
/// already called that and carries no prefix, so a prefix match alone never sees it.
/// <see cref="UpstreamPersistedPrefix"/> never changes either. It is the prefix a stored name (settings id,
/// JSON type name, attribute namespace) must not have, which is a different question from "is this one of
/// our assemblies"; reading <see cref="AssemblyPrefix"/> there would turn the check into "nothing may start
/// with the fork prefix" once the rename lands.
/// The solution file lives here, in a shipped assembly, only because every test project references Sdk.
/// </remarks>
public static class FirstPartyNames
{
    /// <summary>Assembly name prefix shared by every first-party library.</summary>
    public const string AssemblyPrefix = "NexusMods.";

    /// <summary>File-system glob matching the DLLs of the prefixed first-party libraries.</summary>
    public const string AssemblyGlob = AssemblyPrefix + "*.dll";

    /// <summary>Name of the App assembly, which has no prefix.</summary>
    public const string AppAssemblyName = "SilverModManager";

    /// <summary>File name of the solution, used to find the repository root from test binaries.</summary>
    public const string SolutionFile = "NexusMods.App.sln";

    /// <summary>The prefix upstream used for persisted names; a stored name must not start with it.</summary>
    public const string UpstreamPersistedPrefix = "NexusMods.";
}
