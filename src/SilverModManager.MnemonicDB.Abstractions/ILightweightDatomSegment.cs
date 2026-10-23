using System;
using SilverModManager.MnemonicDB.Abstractions.Internals;

namespace SilverModManager.MnemonicDB.Abstractions;

public interface ILightweightDatomSegment : IDisposable
{
    public KeyPrefix KeyPrefix { get; }
    
    public ReadOnlySpan<byte> ValueSpan { get; }
    
    public ReadOnlySpan<byte> ExtraValueSpan { get; }

    /// <summary>
    /// Move to the next datom in the segment, returns false if there are no more datoms
    /// </summary>
    public bool MoveNext();

    /// <summary>
    /// Attempts to fast forward to the next datom that equals the given EntityId
    /// </summary>
    bool FastForwardTo(EntityId from);
}
