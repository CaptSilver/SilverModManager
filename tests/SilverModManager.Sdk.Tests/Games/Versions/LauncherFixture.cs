using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NexusMods.Paths;

namespace SilverModManager.Sdk.Tests.Games.Versions;

/// <summary>
/// Builds launcher-shaped files in a per-test temp directory: tiny managed assemblies compiled with Roslyn
/// (with or without a Win32 VERSIONINFO resource) and hand-written <c>.deps.json</c> files.
/// </summary>
public sealed class LauncherFixture : IDisposable
{
    public const string Name = "Stardew Valley";

    public AbsolutePath Directory { get; }

    public LauncherFixture()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "launcher-version-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(path);
        Directory = FileSystem.Shared.FromUnsanitizedFullPath(path);
    }

    public AbsolutePath Dll => Directory.Combine(Name + ".dll");
    public AbsolutePath Exe => Directory.Combine(Name + ".exe");
    public AbsolutePath DepsJson => Directory.Combine(Name + ".deps.json");

    /// <summary>
    /// Compiles <c>Stardew Valley.dll</c>. <paramref name="withVersionResource"/> adds the Win32 VERSIONINFO
    /// resource that the PE file version APIs read.
    /// </summary>
    public void WriteAssembly(string informationalVersion, string assemblyVersion, bool withVersionResource)
    {
        var source = $$"""
            [assembly: System.Reflection.AssemblyVersion("{{assemblyVersion}}")]
            [assembly: System.Reflection.AssemblyFileVersion("{{assemblyVersion}}")]
            [assembly: System.Reflection.AssemblyInformationalVersion("{{informationalVersion}}")]
            public class Launcher { }
            """;

        var trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? throw new InvalidOperationException("No trusted platform assemblies")).Split(System.IO.Path.PathSeparator);
        var references = trusted
            .Where(p => System.IO.Path.GetFileName(p) is "System.Private.CoreLib.dll" or "System.Runtime.dll")
            .Select(p => MetadataReference.CreateFromFile(p));

        var compilation = CSharpCompilation.Create(
            Name,
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        using var stream = new MemoryStream();
        var resources = withVersionResource
            ? compilation.CreateDefaultWin32Resources(versionResource: true, noManifest: true, manifestContents: null, iconInIcoFormat: null)
            : null;
        var result = compilation.Emit(stream, win32Resources: resources);
        if (!result.Success) throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));

        File.WriteAllBytes(Dll.ToString(), stream.ToArray());
    }

    /// <summary>
    /// Writes a <c>Stardew Valley.deps.json</c> whose project library is <c>Stardew Valley/{version}</c>.
    /// </summary>
    public void WriteDepsJson(string version)
    {
        File.WriteAllText(DepsJson.ToString(), $$"""
            {
              "runtimeTarget": { "name": ".NETCoreApp,Version=v6.0", "signature": "" },
              "targets": {
                ".NETCoreApp,Version=v6.0": {
                  "Stardew Valley/{{version}}": { "dependencies": { "Lib/2.0.0": "2.0.0" } },
                  "Lib/2.0.0": {}
                }
              },
              "libraries": {
                "Stardew Valley/{{version}}": { "type": "project", "serviceable": false, "sha512": "" },
                "Lib/2.0.0": { "type": "package", "serviceable": true, "sha512": "sha512-x" }
              }
            }
            """);
    }

    public void WriteRawDepsJson(string json) => File.WriteAllText(DepsJson.ToString(), json);

    public void Dispose()
    {
        try { System.IO.Directory.Delete(Directory.ToString(), recursive: true); }
        catch (IOException) { }
    }
}
