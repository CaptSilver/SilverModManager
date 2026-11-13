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
/// A folder in the prefix's <c>drive_c</c> that holds one subfolder per installed version, such as
/// <c>Program Files/dotnet/shared/Microsoft.WindowsDesktop.App</c>.
/// </summary>
/// <param name="Path">The folder, relative to <c>drive_c</c>.</param>
/// <param name="VersionPrefix">What a version subfolder's name starts with, such as <c>8.</c> for any 8.x release.</param>
[PublicAPI]
public sealed record PrefixInstalledFolder(string Path, string VersionPrefix);

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

    /// <summary>Every file the install fetches. Empty for a component that is only detected.</summary>
    public ImmutableArray<PrefixDownload> Downloads { get; init; } = [];

    /// <summary>The install, in order. Empty for a component that is only detected.</summary>
    public ImmutableArray<PrefixInstallStep> InstallSteps { get; init; } = [];

    /// <summary>Whether the app can install this component; false means it can only be detected and named.</summary>
    public bool Installable => InstallSteps.Length > 0;

    /// <summary>Environment variables to run the install with.</summary>
    public ImmutableDictionary<string, string> Environment { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// DLL names that must exist as native files in the prefix's system directories. A Wine placeholder with
    /// the same name does not count.
    /// </summary>
    public required ImmutableArray<string> NativeFiles { get; init; }

    /// <summary>
    /// Versioned install folders under <c>drive_c</c> for a component that leaves no DLL in the system
    /// directories, such as a .NET runtime. A matching subfolder is enough evidence on its own.
    /// </summary>
    public ImmutableArray<PrefixInstalledFolder> InstalledFolders { get; init; } = [];

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
    /// The .NET 8 desktop runtime, which WPF and WinForms apps such as Pandora Behaviour Engine+ need. The
    /// installer is the x64 one Microsoft lists for 8.0.34 in its release metadata (releases.json for 8.0).
    /// </summary>
    public static readonly PrefixComponent DotNetDesktop8 = new()
    {
        Id = new PrefixComponentId("dotnetdesktop8"),
        DisplayName = ".NET 8 desktop runtime",
        Downloads =
        [
            new PrefixDownload(
                "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.34/windowsdesktop-runtime-8.0.34-win-x64.exe",
                "37aa8a02acc1973ddd7e3de993a02007ccef26b48fa35b09d087fa8ab4602795",
                "windowsdesktop-runtime-8.0.34-win-x64.exe"),
        ],
        InstallSteps =
        [
            new PrefixInstallStep(PrefixInstallStepKind.RunInstaller, "windowsdesktop-runtime-8.0.34-win-x64.exe", PrefixArchitecture.X64, SilentInstall),
        ],
        // The runtime lives under Program Files\dotnet and puts nothing in system32.
        NativeFiles = [],
        InstalledFolders = [new PrefixInstalledFolder("Program Files/dotnet/shared/Microsoft.WindowsDesktop.App", "8.")],
    };

    /// <summary>
    /// The .NET 6 desktop runtime, which MelonLoader needs to generate the assemblies of an IL2CPP game. The
    /// installer is the x64 one Microsoft lists for 6.0.36 in its release metadata (releases.json for 6.0); the
    /// metadata carries a SHA-512, so the SHA-256 here was computed from the downloaded installer after that matched.
    /// </summary>
    public static readonly PrefixComponent DotNetDesktop6 = new()
    {
        Id = new PrefixComponentId("dotnetdesktop6"),
        DisplayName = ".NET 6 desktop runtime",
        Downloads =
        [
            new PrefixDownload(
                "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/6.0.36/windowsdesktop-runtime-6.0.36-win-x64.exe",
                "0d20debb26fc8b2bc84f25fbd9d4596a6364af8517ebf012e8b871127b798941",
                "windowsdesktop-runtime-6.0.36-win-x64.exe"),
        ],
        InstallSteps =
        [
            new PrefixInstallStep(PrefixInstallStepKind.RunInstaller, "windowsdesktop-runtime-6.0.36-win-x64.exe", PrefixArchitecture.X64, SilentInstall),
        ],
        NativeFiles = [],
        InstalledFolders = [new PrefixInstalledFolder("Program Files/dotnet/shared/Microsoft.WindowsDesktop.App", "6.")],
    };

    // The entries below are only detected and named: their download URLs and hashes are not checked, so the app
    // installs none of them and the hint points at the winetricks verb instead.

    /// <summary>The Visual Basic 6 runtime.</summary>
    public static readonly PrefixComponent VisualBasic6Runtime = DetectOnly("vb6run", "Visual Basic 6 runtime", "msvbvm60.dll");

    /// <summary>The Visual Basic 5 runtime.</summary>
    public static readonly PrefixComponent VisualBasic5Runtime = DetectOnly("vb5run", "Visual Basic 5 runtime", "msvbvm50.dll");

    /// <summary>The Visual C++ 2013 runtime.</summary>
    public static readonly PrefixComponent VisualCpp2013 = DetectOnly("vcrun2013", "Visual C++ 2013 runtime", "msvcr120.dll", "msvcp120.dll");

    /// <summary>The Visual C++ 2012 runtime.</summary>
    public static readonly PrefixComponent VisualCpp2012 = DetectOnly("vcrun2012", "Visual C++ 2012 runtime", "msvcr110.dll", "msvcp110.dll");

    /// <summary>The Visual C++ 2010 runtime.</summary>
    public static readonly PrefixComponent VisualCpp2010 = DetectOnly("vcrun2010", "Visual C++ 2010 runtime", "msvcr100.dll", "msvcp100.dll");

    /// <summary>The Visual C++ 2008 runtime.</summary>
    public static readonly PrefixComponent VisualCpp2008 = DetectOnly("vcrun2008", "Visual C++ 2008 runtime", "msvcr90.dll", "msvcp90.dll");

    /// <summary>The Visual C++ 2005 runtime.</summary>
    public static readonly PrefixComponent VisualCpp2005 = DetectOnly("vcrun2005", "Visual C++ 2005 runtime", "msvcr80.dll", "msvcp80.dll");

    /// <summary>The DirectX 9 June 2010 runtime.</summary>
    public static readonly PrefixComponent DirectX9June2010 = DetectOnly("d3dx9_43", "DirectX 9 June 2010 runtime", "d3dx9_43.dll");

    private static PrefixComponent DetectOnly(string id, string displayName, params string[] nativeFiles) => new()
    {
        Id = new PrefixComponentId(id),
        DisplayName = displayName,
        NativeFiles = [.. nativeFiles],
    };

    /// <summary>
    /// The components the prefix setup verb brings into every game's prefix.
    /// </summary>
    public static ImmutableArray<PrefixComponent> All { get; } = [VisualCppRuntime, D3DCompiler47];

    /// <summary>
    /// Every component that can be detected and installed: <see cref="All"/> plus the ones only a tool needs,
    /// which are installed for that tool and not into every prefix.
    /// </summary>
    public static ImmutableArray<PrefixComponent> Known { get; } =
    [
        .. All, DotNetDesktop8, DotNetDesktop6,
        VisualBasic6Runtime, VisualBasic5Runtime, VisualCpp2013, VisualCpp2012, VisualCpp2010, VisualCpp2008, VisualCpp2005, DirectX9June2010,
    ];

    /// <summary>
    /// The component whose <see cref="PrefixComponent.NativeFiles"/> include this DLL, ignoring case, or null
    /// when none does. Wine's loader errors name the missing DLL, and this turns the name into the verb that fixes it.
    /// </summary>
    public static PrefixComponent? ForNativeFile(string dll)
        => Known.FirstOrDefault(c => c.NativeFiles.Contains(dll, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Finds a component by its id or one of its aliases, ignoring case.
    /// </summary>
    public static bool TryResolve(string name, [NotNullWhen(true)] out PrefixComponent? component)
    {
        foreach (var candidate in Known)
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
