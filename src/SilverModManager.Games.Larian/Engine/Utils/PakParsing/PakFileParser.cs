using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DynamicData.Kernel;
using K4os.Compression.LZ4;
using SilverModManager.Games.Larian.Engine.Utils.LsxXmlParsing;
using NexusMods.Paths;
using ZstdSharp;

namespace SilverModManager.Games.Larian.Engine.Utils.PakParsing;

/// <summary>
/// Class to parse and extract files and data from a bg3 .pak file.
/// Credits to @insomnious for reverse engineering the format and implementing the parser. 
/// </summary>
public static class PakFileParser
{
#region Public Methods

    /// <summary>
    /// Parses a bg3 `.pak` file stream and extracts the metadata from the packed `meta.lsx` file.
    /// </summary>
    /// <param name="pakFileStream">The pak to read.</param>
    /// <param name="keepGameModuleDependencies">
    /// Keeps dependencies on modules the game ships (see <see cref="LsxXmlFormat.DependencyModulesToIgnore"/>)
    /// instead of dropping them, for callers that decide that against the installed game.
    /// </param>
    /// <exception cref="InvalidDataException">In case of errors during parsing</exception>
    public static LspkPackageFormat.PakMetaData ParsePakMeta(Stream pakFileStream, bool keepGameModuleDependencies = false)
    {
        using var br = new BinaryReader(pakFileStream);
        var headerData = ParseHeaderInternal(br);
        var fileList = ParseFileListInternal(br, (long)headerData.FileListOffset, headerData);

        var fileEntryInfo = fileList.FirstOrOptional(f => f.Name.Contains("meta.lsx"));
        if (!fileEntryInfo.HasValue)
        {
            throw new InvalidDataException($"Unable to find `meta.lsx` file in pak archive");
        }

        var metaStream = GetFileEntryStream(br, fileEntryInfo.Value);
        var metaFile = MetaLsxParser.ParseMetaFile(metaStream, keepGameModuleDependencies);

        return new LspkPackageFormat.PakMetaData
        {
            MetaFileData = metaFile,
            ScriptExtenderConfigs = GetScriptExtenderConfigs(fileList, br),
        };
    }

    /// <summary>
    /// Lists the modules a `.pak` holds: one for each `Mods/&lt;Folder&gt;/meta.lsx`. Only the file table and
    /// those small entries are read, so this is cheap on the game's multi-gigabyte paks.
    /// </summary>
    /// <exception cref="InvalidDataException">In case of errors during parsing</exception>
    public static LsxXmlFormat.ModuleShortDesc[] ParseModules(Stream pakFileStream)
    {
        using var br = new BinaryReader(pakFileStream);
        var headerData = ParseHeaderInternal(br);
        var fileList = ParseFileListInternal(br, (long)headerData.FileListOffset, headerData);

        var modules = new List<LsxXmlFormat.ModuleShortDesc>();
        foreach (var entry in fileList.Where(IsModuleMeta))
        {
            using var metaStream = GetFileEntryStream(br, entry);
            modules.Add(MetaLsxParser.ParseMetaFile(metaStream, keepGameModuleDependencies: true).ModuleShortDesc);
        }

        return modules.ToArray();
    }

    private static bool IsModuleMeta(LspkPackageFormat.FileEntryInfoCommon entry)
    {
        var parts = entry.Name.Split('/');
        return parts.Length == 3
               && parts[0].Equals("Mods", StringComparison.OrdinalIgnoreCase)
               && parts[2].Equals("meta.lsx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The extender config files a pak can carry, by the name callers look them up under.</summary>
    private static readonly string[] ExtenderConfigNames = ["ScriptExtender/Config.json", "OsiToolsConfig.json"];

    private static IReadOnlyDictionary<string, LspkPackageFormat.ScriptExtenderConfigMetadata> GetScriptExtenderConfigs(
        List<LspkPackageFormat.FileEntryInfoCommon> fileList, BinaryReader br)
    {
        var configs = new Dictionary<string, LspkPackageFormat.ScriptExtenderConfigMetadata>();
        foreach (var name in ExtenderConfigNames)
        {
            var entry = fileList.FirstOrOptional(f => EntryIs(f.Name, name));
            if (!entry.HasValue) continue;

            var requiredVersion = ReadRequiredVersion(GetFileEntryBytes(br, entry.Value));
            if (requiredVersion.HasValue)
            {
                configs[name] = new LspkPackageFormat.ScriptExtenderConfigMetadata
                {
                    RequiresScriptExtender = true,
                    SeRequiredVersion = requiredVersion.Value,
                };
            }
        }

        return configs;
    }

    // A config sits in a module folder (`Mods/<Folder>/ScriptExtender/Config.json`); the folder name is not part of the key.
    private static bool EntryIs(string entryName, string configName)
        => entryName.Equals(configName, StringComparison.Ordinal)
           || entryName.EndsWith("/" + configName, StringComparison.Ordinal);

    private static Optional<int> ReadRequiredVersion(byte[] json)
    {
        var jsonReader = new Utf8JsonReader(json);
        while (jsonReader.Read())
        {
            if (jsonReader.TokenType != JsonTokenType.PropertyName || jsonReader.GetString() != "RequiredVersion")
                continue;

            jsonReader.Read();
            if (jsonReader.TokenType == JsonTokenType.Number && jsonReader.TryGetInt32(out var requiredVersion))
            {
                return requiredVersion;
            }
        }

        return Optional<int>.None;
    }

#endregion

#region Private Methods

    /// <summary>
    /// Detects the package version the way LSLib does: a v13 trailer at the end of the file first, then the
    /// "LSPK" signature with a version behind it (v10, v15, v16, v18), then no signature at all, where the first
    /// Int32 is the version (v7, v9). The trailer goes first because a v13 package's first bytes are file data,
    /// which can itself be a nested "LSPK" package.
    /// </summary>
    private static LspkPackageFormat.HeaderCommon ParseHeaderInternal(BinaryReader br)
    {
        var header = ReadHeader(br);
        if (header.Flags.HasFlag(LspkPackageFormat.PackageFlags.Solid))
        {
            throw new InvalidDataException("solid packages are not supported");
        }

        return header;
    }

    private static LspkPackageFormat.HeaderCommon ReadHeader(BinaryReader br)
    {
        var length = br.BaseStream.Length;
        const int trailerSize = 8;

        if (length >= trailerSize + LspkPackageFormat.LSPKHeader13.Size)
        {
            br.BaseStream.Seek(length - trailerSize, SeekOrigin.Begin);
            var headerSize = br.ReadInt32();
            var trailerSignature = Encoding.UTF8.GetString(br.ReadBytes(4));
            if (trailerSignature == LspkPackageFormat.HeaderCommon.SIGNATURE_STRING)
            {
                if (headerSize < trailerSize + LspkPackageFormat.LSPKHeader13.Size || headerSize > length)
                {
                    throw new InvalidDataException($"Invalid LSPK v13 header size {headerSize}");
                }

                br.BaseStream.Seek(length - headerSize, SeekOrigin.Begin);
                return new LspkPackageFormat.LSPKHeader13
                {
                    Version = br.ReadUInt32(),
                    FileListOffset = br.ReadUInt32(),
                    FileListSize = br.ReadUInt32(),
                    NumParts = br.ReadUInt16(),
                    Flags = br.ReadByte(),
                    Priority = br.ReadByte(),
                    Md5 = br.ReadBytes(16),
                }.ToCommonHeader();
            }
        }

        br.BaseStream.Seek(0, SeekOrigin.Begin);
        if (length < 8)
        {
            throw new InvalidDataException("Not a valid Larian PAK: the file is too short to hold a header");
        }

        var magic = br.ReadBytes(4);
        var signature = Encoding.UTF8.GetString(magic);

        if (signature == LspkPackageFormat.HeaderCommon.SIGNATURE_STRING)
        {
            var version = br.ReadUInt32();
            switch (version)
            {
                case 10:
                    return new LspkPackageFormat.LSPKHeader10
                    {
                        Version = version,
                        DataOffset = br.ReadUInt32(),
                        FileListSize = br.ReadUInt32(),
                        NumParts = br.ReadUInt16(),
                        Flags = br.ReadByte(),
                        Priority = br.ReadByte(),
                        NumFiles = br.ReadUInt32(),
                    }.ToCommonHeader(fileListOffset: br.BaseStream.Position);
                case 15:
                    return new LspkPackageFormat.LSPKHeader15()
                    {
                        Version = version,
                        FileListOffset = br.ReadUInt64(),
                        FileListSize = br.ReadUInt32(),
                        Flags = br.ReadByte(),
                        Priority = br.ReadByte(),
                        Md5 = br.ReadBytes(16),
                    }.ToCommonHeader();
                case 16:
                case 18:
                    // same as 16 header
                    return new LspkPackageFormat.LSPKHeader16Or18()
                    {
                        Version = version,
                        FileListOffset = br.ReadUInt64(),
                        FileListSize = br.ReadUInt32(),
                        Flags = br.ReadByte(),
                        Priority = br.ReadByte(),
                        Md5 = br.ReadBytes(16),
                        NumParts = br.ReadUInt16(),
                    }.ToCommonHeader();
                default:
                    throw new InvalidDataException($"Unrecognized Pak version: v{version}");
            }
        }

        br.BaseStream.Seek(0, SeekOrigin.Begin);
        var bareVersion = br.ReadUInt32();
        if (bareVersion is not (7 or 9))
        {
            throw new InvalidDataException($"Not a valid Larian PAK. Magic signature `{signature}` does not match expected signature `{LspkPackageFormat.HeaderCommon.SIGNATURE_STRING}`");
        }

        if (length < LspkPackageFormat.LSPKHeader7.Size)
        {
            throw new InvalidDataException($"Not a valid Larian PAK: the v{bareVersion} header is truncated");
        }

        return new LspkPackageFormat.LSPKHeader7
        {
            Version = bareVersion,
            DataOffset = br.ReadUInt32(),
            NumParts = br.ReadUInt32(),
            FileListSize = br.ReadUInt32(),
            LittleEndian = br.ReadByte(),
            NumFiles = br.ReadUInt32(),
        }.ToCommonHeader(fileListOffset: br.BaseStream.Position);
    }

    private static List<LspkPackageFormat.FileEntryInfoCommon> ParseFileListInternal(BinaryReader br, long offset, LspkPackageFormat.HeaderCommon header)
    {
        br.BaseStream.Seek(offset, SeekOrigin.Begin);
        var entrySize = LspkPackageFormat.GetFileEntrySize(header);

        int numOfFiles;
        byte[] tableBytes;
        switch (header.Version)
        {
            case 7 or 9 or 10:
                // Uncompressed, right behind the header.
                var tableSize = RequireTableFits(br, (long)header.NumFiles * entrySize);
                numOfFiles = (int)header.NumFiles;
                tableBytes = br.ReadBytes(tableSize);
                break;
            case 13:
                // LZ4, the entry count in front of FileListSize - 4 compressed bytes.
                numOfFiles = br.ReadInt32();
                if (numOfFiles < 0 || (long)numOfFiles * entrySize > int.MaxValue)
                {
                    throw new InvalidDataException($"The file table claims {numOfFiles} entries");
                }
                var compressedTableSize = RequireTableFits(br, (long)header.FileListSize - 4);
                tableBytes = DecompressFileList(br.ReadBytes(compressedTableSize), numOfFiles, entrySize);
                break;
            default:
                // Assumption that we always have LZ4 for v15-18 (same as LSLib) but could be wrong
                numOfFiles = br.ReadInt32();
                var compressedSize = br.ReadInt32();
                tableBytes = DecompressFileList(br.ReadBytes(compressedSize), numOfFiles, entrySize);
                break;
        }

        // new mem stream from decompress bytes
        using var ms = new MemoryStream(tableBytes);
        using var msr = new BinaryReader(ms);

        // built up list of file entries
        var entries = new List<LspkPackageFormat.FileEntryInfoCommon>(numOfFiles);

        for (var i = 0; i < numOfFiles; i++)
        {
            var entry = ParseFileEntryInternal(msr, (int)header.Version);

            // v7, v9 and v10 offsets are relative to the data section, but only in the package's own part.
            if (header.DataOffset != 0 && entry.ArchivePart == 0)
            {
                entry.OffsetInFile += header.DataOffset;
            }

            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// The legacy table sizes come straight from the header, so a damaged pak, or a foreign file whose first Int32
    /// is 7, would otherwise size a multi-gigabyte read. <paramref name="size"/> counts from the stream position.
    /// </summary>
    private static int RequireTableFits(BinaryReader br, long size)
    {
        var remaining = br.BaseStream.Length - br.BaseStream.Position;
        if (size < 0 || size > remaining)
        {
            throw new InvalidDataException($"The file table needs {size} bytes but the pak has {remaining} left");
        }

        return (int)size;
    }

    private static byte[] DecompressFileList(byte[] compressedBytes, int numOfFiles, int entrySize)
    {
        var decompressedBytes = new byte[numOfFiles * entrySize];

        var numDecodedBytes = LZ4Codec.Decode(
            compressedBytes,
            0,
            compressedBytes.Length,
            decompressedBytes,
            0,
            decompressedBytes.Length
        );

        if (numDecodedBytes != decompressedBytes.Length)
        {
            throw new InvalidDataException($"Decompression failed: decompressed size {decompressedBytes.Length} does not match expected size {numDecodedBytes}");
        }

        return decompressedBytes;
    }

    private static LspkPackageFormat.FileEntryInfoCommon ParseFileEntryInternal(BinaryReader br, int version)
    {
        switch (version)
        {
            case 7:
            case 9:
            {
                return new LspkPackageFormat.FileEntry7
                {
                    Name = br.ReadBytes(256),
                    OffsetInFile = br.ReadUInt32(),
                    SizeOnDisk = br.ReadUInt32(),
                    UncompressedSize = br.ReadUInt32(),
                    ArchivePart = br.ReadUInt32(),
                }.ToCommonFileEntry();
            }
            case 10:
            case 13:
            {
                return new LspkPackageFormat.FileEntry10
                {
                    Name = br.ReadBytes(256),
                    OffsetInFile = br.ReadUInt32(),
                    SizeOnDisk = br.ReadUInt32(),
                    UncompressedSize = br.ReadUInt32(),
                    ArchivePart = br.ReadUInt32(),
                    Flags = br.ReadUInt32(),
                    Crc = br.ReadUInt32(),
                }.ToCommonFileEntry();
            }
            case 15:
            case 16: // same as 15
            {
                return new LspkPackageFormat.FileEntry15Or16
                {
                    Name = br.ReadBytes(256),
                    OffsetInFile = br.ReadUInt64(),
                    SizeOnDisk = br.ReadUInt64(),
                    UncompressedSize = br.ReadUInt64(),
                    ArchivePart = br.ReadUInt32(),
                    Flags = br.ReadUInt32(),
                    Crc = br.ReadUInt32(),
                    Unknown2 = br.ReadUInt32()
                }.ToCommonFileEntry();
            }
            case 18:
            {
                return new LspkPackageFormat.FileEntry18
                {
                    Name = br.ReadBytes(256),
                    OffsetInFile1 = br.ReadUInt32(),
                    OffsetInFile2 = br.ReadUInt16(),
                    ArchivePart = br.ReadByte(),
                    Flags = br.ReadByte(),
                    SizeOnDisk = br.ReadUInt32(),
                    UncompressedSize = br.ReadUInt32()
                }.ToCommonFileEntry();
            }
            default:
                throw new InvalidDataException($"Unrecognized Pak version: v{version}");
        }
    }

    /// <summary>
    /// An entry with a non-zero part lives in a sibling `&lt;base&gt;_&lt;n&gt;.pak`, so its offset means nothing in
    /// the file being read.
    /// </summary>
    private static void RequirePartZero(LspkPackageFormat.FileEntryInfoCommon fileMeta)
    {
        if (fileMeta.ArchivePart > 0)
        {
            throw new InvalidDataException($"{fileMeta.Name} is stored in part {fileMeta.ArchivePart} of a multi-part package, which is not supported");
        }
    }

    private static byte[] GetFileEntryBytes(BinaryReader br, LspkPackageFormat.FileEntryInfoCommon fileMeta)
    {
        RequirePartZero(fileMeta);
        br.BaseStream.Seek((long)fileMeta.OffsetInFile, SeekOrigin.Begin);

        var rawData = br.ReadBytes((int)fileMeta.SizeOnDisk);

        return fileMeta.Flags.Method() switch
        {
            LspkPackageFormat.CompressionMethod.None => rawData,
            LspkPackageFormat.CompressionMethod.LZ4 => DecompressLz4(fileMeta, rawData),
            LspkPackageFormat.CompressionMethod.Zlib => DecompressZlib(fileMeta, rawData),
            LspkPackageFormat.CompressionMethod.Zstd => DecompressZstd(fileMeta, rawData),
            _ => throw new InvalidDataException($"Unsupported compression method {fileMeta.Flags.Method()} for file {fileMeta.Name}")
        };

        byte[] DecompressLz4(LspkPackageFormat.FileEntryInfoCommon fileEntryInfoCommon, byte[] bytes)
        {
            var decompressedBytes = new byte[fileEntryInfoCommon.UncompressedSize];

            var decodedSize = LZ4Codec.Decode(bytes, 0, bytes.Length,
                decompressedBytes, 0, decompressedBytes.Length
            );
            if (decodedSize != decompressedBytes.Length)
            {
                throw new InvalidDataException(
                    $"Failed to extract {fileEntryInfoCommon.Name} from Pak archive: decompressed size {decodedSize} does not match expected size {fileEntryInfoCommon.UncompressedSize}"
                );
            }

            return decompressedBytes;
        }

        byte[] DecompressZlib(LspkPackageFormat.FileEntryInfoCommon fileEntryInfoCommon, byte[] bytes)
        {
            using var ms = new MemoryStream(bytes);
            using var ds = new ZLibStream(ms, CompressionMode.Decompress);
            var decompressedBytes = new byte[fileEntryInfoCommon.UncompressedSize];
            var read = ds.Read(decompressedBytes, 0, decompressedBytes.Length);
            if (read != decompressedBytes.Length)
            {
                throw new InvalidDataException(
                    $"Failed to extract {fileEntryInfoCommon.Name} from Pak archive: decompressed size {read} does not match expected size {fileEntryInfoCommon.UncompressedSize}"
                );
            }

            return decompressedBytes;
        }

        byte[] DecompressZstd(LspkPackageFormat.FileEntryInfoCommon fileEntryInfoCommon, byte[] bytes)
        {
            using var ms = new MemoryStream(bytes);
            using var ds = new DecompressionStream(ms);
            var decompressedBytes = new byte[fileEntryInfoCommon.UncompressedSize];
            var read = ds.Read(decompressedBytes, 0, decompressedBytes.Length);
            if (read != decompressedBytes.Length)
            {
                throw new InvalidDataException(
                    $"Failed to extract {fileEntryInfoCommon.Name} from Pak archive: decompressed size {read} does not match expected size {fileEntryInfoCommon.UncompressedSize}"
                );
            }

            return decompressedBytes;
        }
    }

    private static Stream GetFileEntryStream(BinaryReader br, LspkPackageFormat.FileEntryInfoCommon fileMeta)
    {
        RequirePartZero(fileMeta);
        br.BaseStream.Seek((long)fileMeta.OffsetInFile, SeekOrigin.Begin);

        var rawData = br.ReadBytes((int)fileMeta.SizeOnDisk);

        return fileMeta.Flags.Method() switch
        {
            LspkPackageFormat.CompressionMethod.None => new MemoryStream(rawData),
            LspkPackageFormat.CompressionMethod.LZ4 => DecompressLz4(fileMeta, rawData),
            LspkPackageFormat.CompressionMethod.Zlib => DecompressZlib(fileMeta, rawData),
            LspkPackageFormat.CompressionMethod.Zstd => DecompressZstd(fileMeta, rawData),
            _ => throw new InvalidDataException($"Unsupported compression method {fileMeta.Flags.Method()} for file {fileMeta.Name}")
        };

        Stream DecompressLz4(LspkPackageFormat.FileEntryInfoCommon fileEntryInfoCommon, byte[] bytes)
        {
            var decompressedBytes = new byte[fileEntryInfoCommon.UncompressedSize];

            var decodedSize = LZ4Codec.Decode(bytes, 0, bytes.Length,
                decompressedBytes, 0, decompressedBytes.Length
            );
            if (decodedSize != decompressedBytes.Length)
            {
                throw new InvalidDataException(
                    $"Failed to extract {fileEntryInfoCommon.Name} from Pak archive: decompressed size {decodedSize} does not match expected size {fileEntryInfoCommon.UncompressedSize}"
                );
            }

            return new MemoryStream(decompressedBytes);
        }

        Stream DecompressZlib(LspkPackageFormat.FileEntryInfoCommon fileEntryInfoCommon, byte[] bytes)
        {
            using var ms = new MemoryStream(bytes);
            using var ds = new ZLibStream(ms, CompressionMode.Decompress);
            var decompressedBytes = new byte[fileEntryInfoCommon.UncompressedSize];
            var read = ds.Read(decompressedBytes, 0, decompressedBytes.Length);
            if (read != decompressedBytes.Length)
            {
                throw new InvalidDataException(
                    $"Failed to extract {fileEntryInfoCommon.Name} from Pak archive: decompressed size {read} does not match expected size {fileEntryInfoCommon.UncompressedSize}"
                );
            }

            return new MemoryStream(decompressedBytes);
        }

        Stream DecompressZstd(LspkPackageFormat.FileEntryInfoCommon fileEntryInfoCommon, byte[] bytes)
        {
            using var ms = new MemoryStream(bytes);
            using var ds = new DecompressionStream(ms);
            var decompressedBytes = new byte[fileEntryInfoCommon.UncompressedSize];
            var read = ds.Read(decompressedBytes, 0, decompressedBytes.Length);
            if (read != decompressedBytes.Length)
            {
                throw new InvalidDataException(
                    $"Failed to extract {fileEntryInfoCommon.Name} from Pak archive: decompressed size {read} does not match expected size {fileEntryInfoCommon.UncompressedSize}"
                );
            }

            return new MemoryStream(decompressedBytes);
        }
    }

#endregion
}
