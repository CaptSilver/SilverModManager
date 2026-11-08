using System.Reflection;
using System.Runtime.InteropServices;

namespace SilverModManager.Sdk.Native;

/// <summary>
/// Finds and loads a native library the app ships under <c>runtimes/&lt;rid&gt;/native</c>. The runtime's default probing
/// does not look there for a library the app loads itself.
/// </summary>
public static class BundledNativeLibrary
{
    /// <summary>
    /// The runtime identifiers to look under, most specific first: the runtime's own (which can be distro specific),
    /// then the portable <c>linux-&lt;arch&gt;</c>. A platform with no bundled library simply finds nothing.
    /// </summary>
    public static IReadOnlyList<string> RuntimeIdentifiers() => RuntimeIdentifiers(RuntimeInformation.RuntimeIdentifier, RuntimeInformation.ProcessArchitecture);

    public static IReadOnlyList<string> RuntimeIdentifiers(string runtimeIdentifier, Architecture architecture)
    {
        var portable = "linux-" + architecture.ToString().ToLowerInvariant();
        return string.IsNullOrEmpty(runtimeIdentifier) || runtimeIdentifier == portable ? [portable] : [runtimeIdentifier, portable];
    }

    public static string RelativePath(string runtimeIdentifier, string fileName) => $"runtimes/{runtimeIdentifier}/native/{fileName}";

    public static IReadOnlyList<string> CandidatePaths(string fileName, string assemblyLocation, string baseDirectory)
        => CandidatePaths(fileName, assemblyLocation, baseDirectory, RuntimeIdentifiers());

    /// <summary>
    /// Where the bundled library may sit: beside the assembly, then beside the app, under each runtime identifier in turn.
    /// A single-file publish (the AppImage) reports an empty <see cref="Assembly.Location"/> and keeps native libraries
    /// beside the executable, which <see cref="AppContext.BaseDirectory"/> names.
    /// </summary>
    public static IReadOnlyList<string> CandidatePaths(string fileName, string assemblyLocation, string baseDirectory, IReadOnlyList<string> runtimeIdentifiers)
    {
        var folders = new List<string>();
        var assemblyFolder = Path.GetDirectoryName(assemblyLocation);
        if (!string.IsNullOrEmpty(assemblyFolder)) folders.Add(assemblyFolder);
        if (!string.IsNullOrEmpty(baseDirectory)) folders.Add(baseDirectory);

        return CandidatePaths(fileName, folders, runtimeIdentifiers);
    }

    private static string[] CandidatePaths(string fileName, IReadOnlyList<string> roots, IReadOnlyList<string> runtimeIdentifiers)
        => roots
            .SelectMany(root => runtimeIdentifiers.Select(rid => Path.Combine(root, RelativePath(rid, fileName))))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// The first bundled file <paramref name="exists"/> accepts, looking under each of <paramref name="roots"/> and, within
    /// a root, under each runtime identifier in turn. <paramref name="exists"/> is a parameter so a caller with its own file
    /// system can ask it instead of the disk. <paramref name="pathsTried"/> is every candidate in order, found or not,
    /// so a refusal can name them.
    /// </summary>
    public static string? FindFile(string fileName, IReadOnlyList<string> roots, IReadOnlyList<string> runtimeIdentifiers, Func<string, bool> exists,
        out IReadOnlyList<string> pathsTried)
    {
        var candidates = CandidatePaths(fileName, roots, runtimeIdentifiers);
        pathsTried = candidates;
        return candidates.FirstOrDefault(exists);
    }

    /// <summary>
    /// Loads <paramref name="fileName"/> from the bundled locations, then from the library path. <paramref name="pathsTried"/>
    /// lists every place looked, in order, whether or not it loaded.
    /// </summary>
    public static bool TryLoad(string fileName, Assembly assembly, DllImportSearchPath? searchPath, out nint handle, out IReadOnlyList<string> pathsTried)
        => TryLoad(fileName, assembly, searchPath, RuntimeIdentifiers(), out handle, out pathsTried, out _);

    public static bool TryLoad(string fileName, Assembly assembly, DllImportSearchPath? searchPath, out nint handle, out IReadOnlyList<string> pathsTried,
        out IReadOnlyList<string> loadFailures)
        => TryLoad(fileName, assembly, searchPath, RuntimeIdentifiers(), out handle, out pathsTried, out loadFailures);

    public static bool TryLoad(string fileName, Assembly assembly, DllImportSearchPath? searchPath, IReadOnlyList<string> runtimeIdentifiers,
        out nint handle, out IReadOnlyList<string> pathsTried)
        => TryLoad(fileName, assembly, searchPath, runtimeIdentifiers, out handle, out pathsTried, out _);

    /// <summary>
    /// As above, and <paramref name="loadFailures"/> holds one "path: reason" entry for each bundled file that exists but
    /// would not load (a glibc too old for it, the wrong architecture, a corrupt file), so a caller can tell that apart
    /// from a library that is simply not there.
    /// </summary>
    public static bool TryLoad(string fileName, Assembly assembly, DllImportSearchPath? searchPath, IReadOnlyList<string> runtimeIdentifiers,
        out nint handle, out IReadOnlyList<string> pathsTried, out IReadOnlyList<string> loadFailures)
    {
        var tried = new List<string>();
        var failures = new List<string>();
        pathsTried = tried;
        loadFailures = failures;
        foreach (var candidate in CandidatePaths(fileName, assembly.Location, AppContext.BaseDirectory, runtimeIdentifiers))
        {
            tried.Add(candidate);
            if (!File.Exists(candidate)) continue;
            try
            {
                handle = NativeLibrary.Load(candidate);
                return true;
            }
            catch (Exception failure) when (failure is DllNotFoundException or BadImageFormatException)
            {
                failures.Add($"{candidate}: {failure.Message}");
            }
        }

        tried.Add(fileName);
        return NativeLibrary.TryLoad(fileName, assembly, searchPath, out handle);
    }

    public static string NotFoundMessage(string fileName, string buildScript, IReadOnlyList<string> pathsTried, IReadOnlyList<string>? loadFailures = null)
        => (loadFailures is { Count: > 0 }
            ? $"{fileName} was found but failed to load: {string.Join("; ", loadFailures)}. Tried: {string.Join(", ", pathsTried)}."
            : $"{fileName} was not found. Tried: {string.Join(", ", pathsTried)}.") + $" It is built by {buildScript} and committed under the owning project's runtimes/<rid>/native folder.";
}
