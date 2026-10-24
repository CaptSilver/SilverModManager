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

    private static Optional<Version> ReadPeResource(AbsolutePath file)
    {
        try
        {
            if (!file.FileExists) return Optional<Version>.None;
            var fvi = file.FileInfo.GetFileVersionInfo();
            return IsPresent(fvi.ProductVersion) ? fvi.ProductVersion : IsPresent(fvi.FileVersion) ? fvi.FileVersion : Optional<Version>.None;
        }
        catch (Exception)
        {
            return Optional<Version>.None;
        }
    }

    // On Linux the PE reader falls back to the managed assembly attributes, and reports 0.0.0.0 when the informational
    // version is not a version (or when the file has no version data at all). Metadata uses the same 0.0.0.0 for "unset".
    private static bool IsPresent(Version? version) => version is not null && (version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0)) != (0, 0, 0, 0);

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
                if (value is not null && VersionText.TryParse(value, out var informational) && IsPresent(informational)) return informational;
            }

            // An assembly built without an AssemblyVersion carries 0.0.0.0, which must not hide the deps.json.
            return IsPresent(assembly.Version) ? assembly.Version : Optional<Version>.None;
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
