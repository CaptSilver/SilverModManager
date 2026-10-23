using System.Buffers.Binary;
using NexusMods.Games.FromSoft.Formats;

namespace NexusMods.Games.FromSoft.Tests.Formats.Fixtures;

/// <summary>
/// Builds minimal valid ESD files by hand, not through <see cref="Esd.Write"/>, so the reader is tested against bytes
/// assembled here. It is not an independent implementation of the format: the trailing dummy state row, the pool
/// layout and the -1 zero-length offsets are the writer's conventions, copied because the reader has to accept them.
/// Only real files are outside evidence about the format.
/// </summary>
public static class EsdFixture
{
    /// <summary>The condition-table row count a header declares (descriptor slot 3 of the six size/count pairs).</summary>
    public static uint ConditionCount(byte[] raw) => BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x38));

    /// <summary>The condition-pointer pool slot count a header declares; vanilla declares more than its graph reaches.</summary>
    public static uint DeclaredPoolCount(byte[] raw) => BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x50));

    /// <summary>Depth-first through a condition list and its nested subconditions.</summary>
    public static IEnumerable<Condition> WalkConditions(IEnumerable<Condition> conditions)
    {
        foreach (var c in conditions)
        {
            yield return c;
            foreach (var inner in WalkConditions(c.Subconditions)) yield return inner;
        }
    }

    public static byte[] Corrupt(byte[] raw, int offset, long value, int width)
    {
        var copy = (byte[])raw.Clone();
        if (width == 4) BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(offset), (uint)value);
        else BinaryPrimitives.WriteInt64LittleEndian(copy.AsSpan(offset), value);
        return copy;
    }

    /// <param name="groups">Each group's id and its states as (state id, jump target state id or null).</param>
    /// <param name="extraPoolSlots">Pads the condition-pointer pool with orphaned slots no state reaches, the way vanilla's compiler does.</param>
    public static byte[] Synthetic(
        IReadOnlyList<(int GroupId, IReadOnlyList<(int StateId, int? Target)> States)> groups,
        string name = "t000001000",
        int extraPoolSlots = 0)
    {
        var conditions = new List<(int Group, int Target)>();
        var groupRecords = new List<(int Id, int First, int Count)>();
        var indexOf = new Dictionary<(int, int), int>();
        var flat = 0;
        foreach (var (gid, specs) in groups)
        {
            groupRecords.Add((gid, flat, specs.Count));
            foreach (var (sid, _) in specs) indexOf[(gid, sid)] = flat++;
            if (specs.Count > 1) flat++; // dummy state
        }

        var groupCount = groups.Count;
        var stateCount = flat;
        foreach (var (gid, specs) in groups)
        {
            foreach (var (_, target) in specs)
            {
                if (target is { } t) conditions.Add((gid, t));
            }
        }

        var conditionCount = conditions.Count;
        var poolCount = conditionCount + extraPoolSlots;

        var groupsOff = Esd.InternalHeaderSize;
        var statesOff = groupsOff + groupCount * Esd.GroupSize;
        var condsOff = statesOff + stateCount * Esd.StateSize;
        var callsOff = condsOff + conditionCount * Esd.ConditionSize;
        var poolOff = callsOff; // no commands, so the arg table is empty too
        var blobsOff = poolOff + poolCount * 8;
        byte[] evaluator = [0xA1];
        var nameOff = blobsOff + conditionCount * evaluator.Length;

        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Esd.Magic.ToArray());
        w.Write(1u); w.Write(3u); w.Write(3u); w.Write(0x54u); w.Write(0u); // data size patched at the end
        w.Write(6u);
        foreach (var (size, count) in new[]
                 {
                     (Esd.InternalHeaderSize, 1), (Esd.GroupSize, groupCount), (Esd.StateSize, stateCount),
                     (Esd.ConditionSize, conditionCount), (Esd.CommandCallSize, 0), (Esd.CommandArgSize, 0),
                 })
        {
            w.Write((uint)size);
            w.Write((uint)count);
        }

        var nameLen = name.Length > 0 ? name.Length + 1 : 0;
        w.Write((uint)poolOff); w.Write((uint)poolCount); w.Write((uint)nameOff); w.Write((uint)nameLen);
        var dataEnd = nameOff + nameLen * 2;
        w.Write((uint)dataEnd); w.Write(0u); w.Write((uint)dataEnd); w.Write(0u);

        w.Write(1u);
        w.Write(new byte[16]);
        w.Write(0u);
        w.Write((long)groupsOff); w.Write((long)groupCount); w.Write(nameLen > 0 ? (long)nameOff : -1L); w.Write((long)nameLen);
        w.Write(-1L); w.Write(-1L);

        foreach (var (id, first, count) in groupRecords)
        {
            long off = statesOff + first * Esd.StateSize;
            w.Write((long)id); w.Write(off); w.Write((long)count); w.Write(off);
        }

        // A state's condition-pool offset is a bump-allocator cursor, not a sentinel: real ESDs give a
        // zero-condition state wherever the cursor stands, never -1. The reader gates on the count, but the writer
        // has to reproduce it to round-trip a real file, so this fixture matches.
        var condIndex = 0;
        foreach (var (_, specs) in groups)
        {
            long firstCoOff = 0;
            for (var j = 0; j < specs.Count; j++)
            {
                var (sid, target) = specs[j];
                long coOff = poolOff + condIndex * 8;
                if (j == 0) firstCoOff = coOff;
                WriteState(w, sid, coOff, target is null ? 0 : 1);
                if (target is not null) condIndex++;
            }

            if (specs.Count > 1)
                WriteState(w, specs[0].StateId, firstCoOff, specs[0].Target is null ? 0 : 1);
        }

        for (var i = 0; i < conditions.Count; i++)
        {
            var (gid, target) = conditions[i];
            w.Write((long)(statesOff + indexOf[(gid, target)] * Esd.StateSize));
            w.Write(-1L); w.Write(0L); w.Write(-1L); w.Write(0L);
            w.Write((long)(blobsOff + i * evaluator.Length)); w.Write((long)evaluator.Length);
        }

        for (var i = 0; i < poolCount; i++)
        {
            // Padding slots point at the first condition row: orphaned, but a valid pointer, which is what vanilla leaves behind.
            var row = conditionCount > 0 ? Math.Min(i, conditionCount - 1) : 0;
            w.Write((long)(condsOff + row * Esd.ConditionSize));
        }

        for (var i = 0; i < conditionCount; i++) w.Write(evaluator);
        if (nameLen > 0)
        {
            w.Write(System.Text.Encoding.Unicode.GetBytes(name));
            w.Write((ushort)0);
        }

        var bytes = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x14), (uint)(bytes.Length - Esd.HeaderSize));
        return bytes;
    }

    private static void WriteState(BinaryWriter w, int id, long coOff, long conditionCount)
    {
        w.Write((long)id); w.Write(coOff); w.Write(conditionCount);
        w.Write(-1L); w.Write(0L); w.Write(-1L); w.Write(0L); w.Write(-1L); w.Write(0L);
    }
}
