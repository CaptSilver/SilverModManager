using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DynamicData.Kernel;
using NexusMods.Paths;

namespace SilverModManager.Sdk.Games.Versions;

public sealed class LauncherVersionReader : ILauncherVersionReader
{
    public LauncherVersion Read(AbsolutePath launcher)
    {
        var pe = ReadPeResource(launcher);
        if (pe.HasValue) return new LauncherVersion(pe, LauncherVersionSource.PeResource);

        var stem = launcher.Parent.Combine(launcher.GetFileNameWithoutExtension());

        var metadata = ReadAssemblyMetadata(stem.AppendExtension(new Extension(".dll")));
        if (metadata.HasValue) return new LauncherVersion(metadata, LauncherVersionSource.AssemblyMetadata);

        var deps = DepsJsonVersion.TryRead(stem.AppendExtension(new Extension(".deps.json")));
        if (deps.HasValue) return new LauncherVersion(deps, LauncherVersionSource.DepsJson);

        return LauncherVersion.Unknown;
    }

    private static Optional<Version> ReadPeResource(AbsolutePath file) => GameExecutableVersion.Read(file);

    /// <summary>
    /// Reads the version from the managed metadata without loading the assembly: the informational version
    /// first (it carries the product's own number), then the assembly version.
    /// </summary>
    private static Optional<Version> ReadAssemblyMetadata(AbsolutePath dll)
    {
        try
        {
            if (!dll.FileExists) return Optional<Version>.None;

            using var stream = dll.Read();
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata) return Optional<Version>.None;

            var reader = peReader.GetMetadataReader();
            var assembly = reader.GetAssemblyDefinition();

            foreach (var handle in assembly.GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(handle);
                if (!IsInformationalVersion(reader, attribute)) continue;

                var blob = reader.GetBlobReader(attribute.Value);
                if (blob.ReadUInt16() != 1) continue; // custom attribute prolog
                var value = blob.ReadSerializedString();
                if (value is not null && VersionText.TryParse(value, out var informational) && GameExecutableVersion.IsSet(informational)) return informational;
            }

            // An assembly built without an AssemblyVersion carries 0.0.0.0, which must not hide the deps.json.
            return GameExecutableVersion.IsSet(assembly.Version) ? assembly.Version : Optional<Version>.None;
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Optional<Version>.None;
        }
    }

    private static bool IsInformationalVersion(MetadataReader reader, CustomAttribute attribute)
    {
        if (attribute.Constructor.Kind != HandleKind.MemberReference) return false;
        var constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
        if (constructor.Parent.Kind != HandleKind.TypeReference) return false;
        var type = reader.GetTypeReference((TypeReferenceHandle)constructor.Parent);
        return reader.GetString(type.Name) == "AssemblyInformationalVersionAttribute"
               && reader.GetString(type.Namespace) == "System.Reflection";
    }
}
