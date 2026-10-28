using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;

namespace SilverModManager.Sdk.Hashes;

/// <summary>
/// Stores a <see cref="FileIdentity"/> as a blob: the kind byte, then the digest.
/// </summary>
public class FileIdentityAttribute(string ns, string name) : ScalarAttribute<FileIdentity, Memory<byte>, BlobSerializer>(ns, name)
{
    /// <inheritdoc/>
    protected override Memory<byte> ToLowLevel(FileIdentity value)
    {
        var buffer = new byte[1 + value.Digest.Length];
        value.Write(buffer);
        return buffer;
    }

    /// <inheritdoc/>
    protected override FileIdentity FromLowLevel(Memory<byte> value, AttributeResolver resolver) => FileIdentity.Read(value.Span);
}
