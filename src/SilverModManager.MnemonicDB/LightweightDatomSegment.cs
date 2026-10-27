using System;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Internals;

namespace SilverModManager.MnemonicDB;

/// <summary>
/// A very lightweight wrapper over a ref datom enumerator
/// </summary>
internal sealed class LightweightDatomSegment<TEnumerator, TDescriptor> : ILightweightDatomSegment, IDisposable
  where TEnumerator : IRefDatomEnumerator
  where TDescriptor : ISliceDescriptor
{
    private TEnumerator _enumerator;
    private TDescriptor _descriptor;
    
    private State _state;

    private enum State : byte
    {
        NotStarted,
        InProgress,
        Finished
    }

    public LightweightDatomSegment(TEnumerator enumerator, TDescriptor descriptor)
    {
        _enumerator = enumerator;
        _descriptor = descriptor;
        _state = State.NotStarted;
    }
    
    public KeyPrefix KeyPrefix => _enumerator.KeyPrefix;
    public ReadOnlySpan<byte> ValueSpan => _enumerator.ValueSpan.Span;
    public ReadOnlySpan<byte> ExtraValueSpan => _enumerator.ExtraValueSpan.Span;
    
    public bool MoveNext()
    {
        if (_state == State.Finished)
            return false;
        _state = State.InProgress;
        return _enumerator.MoveNext(_descriptor);
    }


    public bool FastForwardTo(EntityId eid)
    {
        if (_state == State.Finished)
        {
            return false;
        }

        if (_state == State.NotStarted)
        {
            if (!MoveNext())
            {
                Finish();
                return false; // No items to process
            }
        }

        while (_enumerator.KeyPrefix.E < eid)
        {
            if (!_enumerator.MoveNext(_descriptor))
            {
                Finish();
                return false; // No more items
            }
        }

        return _enumerator.KeyPrefix.E == eid;
    }

    public void Dispose() => Finish();

    /// <summary>
    /// Releases the native iterator the first time it is called. Running off the end of the data and the caller's
    /// Dispose both end up here, so whichever comes second must not free the iterator again.
    /// </summary>
    private void Finish()
    {
        if (_state == State.Finished)
            return;
        _state = State.Finished;
        _enumerator.Dispose();
    }
}
