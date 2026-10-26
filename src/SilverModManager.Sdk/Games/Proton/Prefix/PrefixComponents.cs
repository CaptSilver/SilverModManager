using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// The stable name of something a Wine prefix can need installed, such as the Visual C++ runtime.
/// </summary>
/// <param name="Value">The canonical name, which is also the winetricks verb.</param>
[PublicAPI]
public readonly record struct PrefixComponentId(string Value)
{
    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>
/// Which bitness of the prefix a download or install step is for.
/// </summary>
[PublicAPI]
public enum PrefixArchitecture
{
    /// <summary>32-bit code, which lives in <c>syswow64</c> in a 64-bit prefix.</summary>
    X86,

    /// <summary>64-bit code, which lives in <c>system32</c>.</summary>
    X64,
}

/// <summary>
/// A file a component installs from. The URL never changes what it returns: it names a version or a commit,
/// never a branch or a redirect.
/// </summary>
/// <param name="Url">Where to fetch it.</param>
/// <param name="Sha256">Lowercase hex SHA-256 the downloaded bytes must have.</param>
/// <param name="FileName">The name to save it under, which install steps refer to.</param>
[PublicAPI]
public sealed record PrefixDownload(string Url, string Sha256, string FileName);

/// <summary>
/// What a <see cref="PrefixInstallStep"/> does with its file.
/// </summary>
[PublicAPI]
public enum PrefixInstallStepKind
{
    /// <summary>Run the downloaded installer inside the prefix.</summary>
    RunInstaller,

    /// <summary>Copy the downloaded DLL into the prefix's system directory for the step's architecture.</summary>
    CopyDll,
}

/// <summary>
/// One action of a component's install, in order.
/// </summary>
/// <param name="Kind">What to do.</param>
/// <param name="FileName">The <see cref="PrefixDownload.FileName"/> it works on.</param>
/// <param name="Architecture">The bitness of the file.</param>
/// <param name="Arguments">Command line for <see cref="PrefixInstallStepKind.RunInstaller"/>.</param>
/// <param name="DestinationName">File name to copy to for <see cref="PrefixInstallStepKind.CopyDll"/>.</param>
[PublicAPI]
public sealed record PrefixInstallStep(
    PrefixInstallStepKind Kind,
    string FileName,
    PrefixArchitecture Architecture,
    ImmutableArray<string> Arguments = default,
    string? DestinationName = null)
{
    /// <summary>
    /// The installer's arguments, empty when none.
    /// </summary>
    public ImmutableArray<string> Arguments { get; } = Arguments.IsDefault ? [] : Arguments;
}

/// <summary>
/// A component the prefix can be checked for and installed: what it is called, where its files come from
/// and how to tell it is already there.
/// </summary>
[PublicAPI]
public sealed record PrefixComponent
{
    /// <summary>The canonical id.</summary>
    public required PrefixComponentId Id { get; init; }

    /// <summary>Other winetricks verbs that mean the same component.</summary>
    public ImmutableArray<string> Aliases { get; init; } = [];

    /// <summary>The name shown to the user.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Every file the install fetches.</summary>
    public required ImmutableArray<PrefixDownload> Downloads { get; init; }

    /// <summary>The install, in order.</summary>
    public required ImmutableArray<PrefixInstallStep> InstallSteps { get; init; }

    /// <summary>Environment variables to run the install with.</summary>
    public ImmutableDictionary<string, string> Environment { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// DLL names that must exist as native files in the prefix's system directories. A Wine placeholder with
    /// the same name does not count.
    /// </summary>
    public required ImmutableArray<string> NativeFiles { get; init; }

    /// <summary>
    /// Registry key fragments, each a set that must all occur in one <c>system.reg</c> key name (compared
    /// case-insensitively, with the file's doubled backslashes). Any one set matching is enough. Empty when
    /// the component leaves no registry trace, so the native files alone are the evidence.
    /// </summary>
    public ImmutableArray<ImmutableArray<string>> RegistryKeys { get; init; } = [];

    /// <summary>The winetricks verbs that name this component, the id included.</summary>
    public IEnumerable<string> WinetricksVerbs => Aliases.Prepend(Id.Value);
}

/// <summary>
/// The components SilverModManager knows how to detect and install in a prefix.
/// </summary>
[PublicAPI]
public static class PrefixComponents
{
    // Microsoft's versioned (immutable) download links for the Visual Studio 2015-2026 v14 runtime.
    private const string VcRedistBase = "https://download.visualstudio.microsoft.com/download/pr/";

    // Pinned to a commit of mozilla/fxc2 because raw/master moves under a fixed hash.
    private const string Fxc2Base = "https://github.com/mozilla/fxc2/raw/807d26f4e4e9e9d0d0d6c1e05493b28eafb20e91/dll/";

    private static readonly ImmutableArray<string> SilentInstall = ["/install", "/quiet", "/norestart"];

    /// <summary>
    /// The Visual C++ 2015-2022 (v14) runtime. vcrun2015, vcrun2019 and vcrun2022 are one runtime.
    /// </summary>
    public static readonly PrefixComponent VisualCppRuntime = new()
    {
        Id = new PrefixComponentId("vcrun2022"),
        Aliases = ["vcrun2019", "vcrun2015"],
        DisplayName = "Visual C++ 2015-2022 runtime",
        Downloads =
        [
            new PrefixDownload(
                VcRedistBase + "355d2512-13c2-400a-bf9f-8a296abb5932/F0BAB33A302B3CDB2E11113760D016F54FD3D2632C65BA7834FAC4F0ABD7F1A3/VC_redist.x86.exe",
                "f0bab33a302b3cdb2e11113760d016f54fd3d2632c65ba7834fac4f0abd7f1a3",
                "VC_redist.x86.exe"),
            new PrefixDownload(
                VcRedistBase + "ebdab8e5-1d7b-4d9f-a11b-cbb1720c3b12/843068991DAAA1F73AD9F6239BCE4D0F6A07A51F18C37EA2A867E9BECA71295C/VC_redist.x64.exe",
                "843068991daaa1f73ad9f6239bce4d0f6a07a51f18c37ea2a867e9beca71295c",
                "VC_redist.x64.exe"),
        ],
        InstallSteps =
        [
            new PrefixInstallStep(PrefixInstallStepKind.RunInstaller, "VC_redist.x86.exe", PrefixArchitecture.X86, SilentInstall),
            new PrefixInstallStep(PrefixInstallStepKind.RunInstaller, "VC_redist.x64.exe", PrefixArchitecture.X64, SilentInstall),
        ],
        // Wine's mshtml stub makes the installer's bundled UI hang waiting for Gecko.
        Environment = ImmutableDictionary<string, string>.Empty.Add(WineParser.WineDllOverridesEnvironmentVariableName, "mshtml=d"),
        NativeFiles = ["vcruntime140.dll", "msvcp140.dll"],
        RegistryKeys =
        [
            ["Installer\\\\Dependencies", "VC,redist"],
            ["VisualStudio\\\\14.0\\\\VC\\\\Runtimes"],
        ],
    };

    /// <summary>
    /// The DirectX shader compiler, version 47.
    /// </summary>
    public static readonly PrefixComponent D3DCompiler47 = new()
    {
        Id = new PrefixComponentId("d3dcompiler_47"),
        DisplayName = "DirectX shader compiler 47",
        Downloads =
        [
            new PrefixDownload(Fxc2Base + "d3dcompiler_47_32.dll", "2ad0d4987fc4624566b190e747c9d95038443956ed816abfd1e2d389b5ec0851", "d3dcompiler_47_32.dll"),
            new PrefixDownload(Fxc2Base + "d3dcompiler_47.dll", "4432bbd1a390874f3f0a503d45cc48d346abc3a8c0213c289f4b615bf0ee84f3", "d3dcompiler_47.dll"),
        ],
        InstallSteps =
        [
            new PrefixInstallStep(PrefixInstallStepKind.CopyDll, "d3dcompiler_47_32.dll", PrefixArchitecture.X86, DestinationName: "d3dcompiler_47.dll"),
            new PrefixInstallStep(PrefixInstallStepKind.CopyDll, "d3dcompiler_47.dll", PrefixArchitecture.X64, DestinationName: "d3dcompiler_47.dll"),
        ],
        NativeFiles = ["d3dcompiler_47.dll"],
    };

    /// <summary>
    /// Every known component.
    /// </summary>
    public static ImmutableArray<PrefixComponent> All { get; } = [VisualCppRuntime, D3DCompiler47];

    /// <summary>
    /// Finds a component by its id or one of its aliases, ignoring case.
    /// </summary>
    public static bool TryResolve(string name, [NotNullWhen(true)] out PrefixComponent? component)
    {
        foreach (var candidate in All)
        {
            if (!candidate.WinetricksVerbs.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            component = candidate;
            return true;
        }

        component = null;
        return false;
    }

    /// <summary>
    /// The component for an id.
    /// </summary>
    /// <exception cref="ArgumentException">The id is not in the table.</exception>
    public static PrefixComponent Get(PrefixComponentId id)
    {
        return TryResolve(id.Value, out var component)
            ? component
            : throw new ArgumentException($"'{id}' is not a known prefix component.", nameof(id));
    }
}
