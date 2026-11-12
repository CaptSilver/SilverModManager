using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// An intrinsic file that records what is written to it. Called with the rendered bytes in the write's transaction,
/// before the file is replaced; that transaction commits only once every action of the apply ran, so the record
/// lands only for a write that landed, together with the disk state row for the same bytes. Bytes stored here are
/// pinned through <see cref="Deployment.TransactionPins"/> until the transaction is done.
/// </summary>
public interface IIntrinsicFileWithWriteRecord : IIntrinsicFile
{
    Task RecordWrite(IntrinsicWrite write);
}

/// <summary>One write of an intrinsic file: the rendered bytes and the apply writing them.</summary>
public sealed record IntrinsicWrite(byte[] Bytes, Loadout.ReadOnly Loadout, ITransaction Transaction);
