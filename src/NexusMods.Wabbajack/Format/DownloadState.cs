using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;

namespace NexusMods.Wabbajack.Format;

/// <summary>Where an archive comes from, as the list records it.</summary>
public abstract record DownloadState;

public sealed record NexusState(string GameName, long ModId, long FileId, string? Name, string? Version, string? Author, bool IsNsfw) : DownloadState;

public sealed record HttpState(Uri Url, string[] Headers) : DownloadState;

public sealed record WabbajackCdnState(Uri Url) : DownloadState;

public sealed record GameFileSourceState(string Game, RelativePath GameFile, Hash XxHash64, string GameVersion) : DownloadState;

public sealed record ManualState(Uri Url, string Prompt) : DownloadState;

public sealed record MegaState(Uri Url) : DownloadState;

public sealed record GoogleDriveState(string Id) : DownloadState;

public sealed record MediaFireState(Uri Url) : DownloadState;

public sealed record ModDbState(Uri Url) : DownloadState;

/// <summary>Url comes from the state's <c>IPS4Url</c>, FileName from its <c>IPS4File</c>; either may be absent.</summary>
public sealed record LoversLabState(Uri? Url, string? FileName) : DownloadState;

public sealed record VectorPlexusState(Uri? Url, string? FileName) : DownloadState;

/// <summary>
/// A source the app cannot fetch from: Bethesda, TESAlliance, the old LoversLab, or a type this reader does not
/// know. <paramref name="TypeName"/> is the <c>$type</c> as written.
/// </summary>
public sealed record UnsupportedState(string TypeName, string? Url) : DownloadState;
