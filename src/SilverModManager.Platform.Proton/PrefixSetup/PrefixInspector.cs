using System.Text;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.PrefixSetup;

/// <inheritdoc/>
internal sealed class PrefixInspector : IPrefixInspector
{
    private static readonly byte[][] PlaceholderMarkers = [Encoding.ASCII.GetBytes("Wine placeholder DLL"), Encoding.ASCII.GetBytes("Wine builtin DLL")];

    // A real PE version resource carries this UTF-16 root key; Wine's stub DLLs do not.
    private static readonly byte[] VersionResourceMarker = Encoding.Unicode.GetBytes("VS_VERSION_INFO");

    private enum FileKind { Absent, Placeholder, Native, Unverified }

    private static readonly string[] SystemDirectories = ["drive_c/windows/system32", "drive_c/windows/syswow64"];

    /// <inheritdoc/>
    public IReadOnlyList<PrefixComponentStatus> Inspect(ResolvedPrefix prefix, IReadOnlyList<PrefixComponentId> components)
    {
        var registryKeys = ReadRegistryKeys(prefix.Path.Combine("system.reg"));
        var winetricksVerbs = WineParser.ParseWinetricksLogFile(WineParser.GetWinetricksLogFilePath(prefix.Path));

        return components
            .Select(id => InspectOne(prefix, PrefixComponents.Get(id), registryKeys, winetricksVerbs))
            .ToList();
    }

    private static PrefixComponentStatus InspectOne(
        ResolvedPrefix prefix,
        PrefixComponent component,
        IReadOnlyList<string> registryKeys,
        ICollection<string> winetricksVerbs)
    {
        var files = new List<(string Where, FileKind Kind)>();
        foreach (var name in component.NativeFiles)
        {
            foreach (var directory in SystemDirectories)
                files.Add(($"{directory}/{name}", Classify(prefix.Path.Combine($"{directory}/{name}"))));
        }

        var native = files.Where(f => f.Kind == FileKind.Native).ToList();
        var placeholders = files.Where(f => f.Kind == FileKind.Placeholder).ToList();
        var unverified = files.Where(f => f.Kind == FileKind.Unverified).ToList();
        var needsRegistryEntry = component.RegistryKeys.Length > 0;
        var hasRegistryEntry = component.RegistryKeys.Any(set => registryKeys.Any(key => set.All(part => key.Contains(part, StringComparison.OrdinalIgnoreCase))));
        var winetricksHint = component.WinetricksVerbs.Any(winetricksVerbs.Contains);

        PrefixComponentStatus Result(PrefixComponentState state, string evidence) => new(component.Id, state, evidence);

        if (placeholders.Count > 0 && native.Count > 0)
            return Result(PrefixComponentState.Unknown, $"native {native[0].Where} but a Wine placeholder at {placeholders[0].Where}");

        if (unverified.Count > 0)
            return Result(PrefixComponentState.Unknown, $"{unverified[0].Where} is neither a Wine placeholder nor a DLL with a version resource");

        if (native.Count > 0)
        {
            if (!needsRegistryEntry || hasRegistryEntry)
                return Result(PrefixComponentState.Installed, $"native {native[0].Where}" + (needsRegistryEntry ? " and its registry entry" : " with a version resource"));
            return Result(PrefixComponentState.Unknown, $"native {native[0].Where} but no registry entry in system.reg");
        }

        if (hasRegistryEntry)
            return Result(PrefixComponentState.Unknown, "registry entry in system.reg but no native DLL");

        if (winetricksHint)
            return Result(PrefixComponentState.Unknown, "winetricks.log only");

        return Result(PrefixComponentState.Missing, placeholders.Count > 0 ? $"only Wine placeholder DLLs, such as {placeholders[0].Where}" : "no DLL or registry entry found");
    }

    private static FileKind Classify(AbsolutePath path)
    {
        if (!path.FileExists) return FileKind.Absent;

        using var stream = path.Read();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);

        foreach (var marker in PlaceholderMarkers)
        {
            if (bytes.IndexOf(marker) >= 0) return FileKind.Placeholder;
        }

        return bytes.IndexOf(VersionResourceMarker) >= 0 ? FileKind.Native : FileKind.Unverified;
    }

    private static List<string> ReadRegistryKeys(AbsolutePath path)
    {
        var keys = new List<string>();
        if (!path.FileExists) return keys;

        using var reader = new StreamReader(path.Read(), Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith('[')) keys.Add(line);
        }

        return keys;
    }
}
