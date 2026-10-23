namespace NexusMods.Wabbajack.Transforms;

/// <summary>
/// What a re-encoded texture must look like. <paramref name="MipLevels"/> of 0 asks for a full chain.
/// </summary>
public readonly record struct DdsTarget(int Width, int Height, int MipLevels, DxgiFormat Format);
