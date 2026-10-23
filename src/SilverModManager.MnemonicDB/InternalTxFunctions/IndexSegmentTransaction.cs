using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using SilverModManager.MnemonicDB.Storage;

namespace SilverModManager.MnemonicDB.InternalTxFunctions;

/// <summary>
/// A standard transaction that simply processes an index segment
/// </summary>
internal sealed class IndexSegmentTransaction : AInternalFn
{
    private readonly IndexSegment _indexSegment;

    /// <summary>
    /// A standard transaction that simply processes an index segment
    /// </summary>
    public IndexSegmentTransaction(IndexSegment indexSegment)
    {
        _indexSegment = indexSegment;
    }

    /// <inheritdoc />
    public override void Execute(DatomStore store)
    {
        store.LogDatoms(_indexSegment);
    }
}
