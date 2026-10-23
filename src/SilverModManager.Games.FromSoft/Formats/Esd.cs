using System.Buffers.Binary;
using System.Text;

namespace NexusMods.Games.FromSoft.Formats;

/// <summary>One argument of a command call: an opaque bytecode expression.</summary>
/// <param name="Order">Row index in the source table; null for an argument a merge created.</param>
/// <param name="BlobOrder">Where the bytecode sat in the source's payload region, so a rewrite keeps the same layout.</param>
public sealed record CommandArg(byte[] Bytecode, int? Order = null, long? BlobOrder = null);

/// <param name="Bank">Bank 6 ids name a state group, which makes it the one bank a regraft has to rewrite.</param>
public sealed record CommandCall(int Bank, int Id, IReadOnlyList<CommandArg>? Args = null, int? Order = null)
{
    public IReadOnlyList<CommandArg> Args { get; init; } = Args ?? [];
}

/// <param name="Target">The id of the state in the same group this condition jumps to, or null for no jump.</param>
public sealed record Condition(
    long? Target,
    IReadOnlyList<CommandCall>? PassCommands = null,
    IReadOnlyList<Condition>? Subconditions = null,
    byte[]? Evaluator = null,
    int? Order = null,
    long? BlobOrder = null)
{
    public IReadOnlyList<CommandCall> PassCommands { get; init; } = PassCommands ?? [];
    public IReadOnlyList<Condition> Subconditions { get; init; } = Subconditions ?? [];
    public byte[] Evaluator { get; init; } = Evaluator ?? [];
}

public sealed record State(
    long Id,
    IReadOnlyList<Condition>? Conditions = null,
    IReadOnlyList<CommandCall>? Entry = null,
    IReadOnlyList<CommandCall>? Exit = null,
    IReadOnlyList<CommandCall>? While = null,
    int? Order = null)
{
    public IReadOnlyList<Condition> Conditions { get; init; } = Conditions ?? [];
    public IReadOnlyList<CommandCall> Entry { get; init; } = Entry ?? [];
    public IReadOnlyList<CommandCall> Exit { get; init; } = Exit ?? [];
    public IReadOnlyList<CommandCall> While { get; init; } = While ?? [];
}

public sealed record StateGroup(long Id, IReadOnlyList<State> States, int? Order = null);

/// <param name="Unk">The four u32s at internal header +4; their meaning is unknown.</param>
public sealed record EsdFile(IReadOnlyList<StateGroup> Groups, string Name, IReadOnlyList<uint> Unk);

/// <summary>
/// ESD ("EZState"): the compiled state machines behind dialogue and grace menus. A graph, unlike every other format
/// here: conditions carry jump targets that name a state by byte offset, so a state cannot be lifted from one file
/// to another the way a param row or an FMG string can. The reader resolves targets to state ids and the writer
/// re-derives every offset; nothing ever patches an offset in place.
/// </summary>
/// <remarks>
/// Records keep the index they had in their source table (<c>Order</c>). The writer emits in ascending original order
/// so a round-trip reproduces the source layout exactly, without needing FromSoft's emission rule, which nobody has
/// recovered. Records created by a merge carry a null order and are appended after.
/// </remarks>
public static class Esd
{
    public const int HeaderSize = 0x6C;
    public const int InternalHeaderSize = 72;
    public const int GroupSize = 32;
    public const int StateSize = 72;
    public const int ConditionSize = 56;
    public const int CommandCallSize = 24;
    public const int CommandArgSize = 16;

    /// <summary>Bank 6 command ids name a state group. Banks 1, 5 and 7 never collide with group ids.</summary>
    public const int CallBank = 6;

    private const int MaxConditionDepth = 1000;

    private static readonly UnicodeEncoding Utf16 = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);

    public static ReadOnlySpan<byte> Magic => "fsSL"u8;

    // ---------------------------------------------------------------- read

    /// <summary>Parses an ESD. Throws rather than returning a partial graph.</summary>
    /// <exception cref="EsdException">The data is not an ESD, or is malformed.</exception>
    public static EsdFile Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || !data[..4].SequenceEqual(Magic))
            throw new EsdException("not an ESD (bad magic)");
        var dataSize = U32(data, 0x14);
        if (HeaderSize + (long)dataSize != data.Length)
            throw new EsdException($"ESD header declares {dataSize} data bytes but the file holds {data.Length - HeaderSize} -- the layout was misread");

        long groupCount = U32(data, 0x28), stateCount = U32(data, 0x30), condCount = U32(data, 0x38);
        long callCount = U32(data, 0x40), argCount = U32(data, 0x48);
        long nameOff = U32(data, 0x54), nameLen = U32(data, 0x58);

        // Table bases, relative to the data after the file header; the regions are contiguous in this order.
        const long groupsAt = InternalHeaderSize;
        var statesAt = groupsAt + groupCount * GroupSize;
        var condsAt = statesAt + stateCount * StateSize;
        var callsAt = condsAt + condCount * ConditionSize;
        var argsAt = callsAt + callCount * CommandCallSize;
        var poolAt = argsAt + argCount * CommandArgSize;

        // Both header checks above compare header fields with each other, so a crafted header can keep them
        // consistent while inflating a count far past the buffer. Bound every table against the real length.
        long dataLen = data.Length - HeaderSize;
        foreach (var (label, count, end) in new[]
                 {
                     ("state group", groupCount, statesAt), ("state", stateCount, condsAt),
                     ("condition", condCount, callsAt), ("command call", callCount, argsAt),
                     ("command arg", argCount, poolAt),
                 })
        {
            if (end > dataLen)
                throw new EsdException($"{label} table (count={count}) runs past the end of the {data.Length}-byte file");
        }

        if (U32(data, 0x4C) != poolAt)
            throw new EsdException($"condition-offset pool is at {U32(data, 0x4C)} but the tables end at {poolAt} -- the tables do not tile the file");

        var reader = new Reader(data.ToArray(), dataLen, statesAt, condsAt, callsAt, argsAt, poolAt, stateCount, condCount, callCount, argCount);
        return reader.Run(groupCount, nameOff, nameLen);
    }

    private sealed class Reader(
        byte[] file, long dataLen, long statesAt, long condsAt, long callsAt, long argsAt, long poolAt,
        long stateCount, long condCount, long callCount, long argCount)
    {
        private ReadOnlySpan<byte> Data => file;

        private static long Abs(long rel) => HeaderSize + rel;

        // Every run below is addressed by numbers the file supplies. The table checks bound the tables, not where a
        // run inside one starts, so an unaligned or out-of-table run would otherwise read whatever bytes sit there.
        private static long RunStart(string what, long offset, long baseAt, int size, long count, long n)
        {
            if (n <= 0) return 0;
            if (offset < baseAt || (offset - baseAt) % size != 0)
                throw new EsdException($"{what} run starts at {offset}, which is not a row boundary of the table at {baseAt}");
            var first = (offset - baseAt) / size;
            if (n > count - first)
                throw new EsdException($"{what} run of {n} at row {first} runs past the end of the {count}-row table");
            return first;
        }

        // A slice past the end must be refused: a short evaluator decodes to a different machine.
        private byte[] Blob(long offset, long length, string what)
        {
            if (length <= 0) return [];
            if (offset < 0 || offset > dataLen || length > dataLen - offset)
                throw new EsdException($"{what} at {offset} ({length} bytes) runs past the end of the {file.Length}-byte file");
            return Data.Slice((int)Abs(offset), (int)length).ToArray();
        }

        public EsdFile Run(long groupCount, long nameOff, long nameLen)
        {
            var unk = new[] { U32(Data, (int)Abs(4)), U32(Data, (int)Abs(8)), U32(Data, (int)Abs(12)), U32(Data, (int)Abs(16)) };
            var name = "";
            if (nameLen > 0)
            {
                var raw = Blob(nameOff, (nameLen - 1) * 2, "name"); // the NUL is not stored
                try
                {
                    name = Utf16.GetString(raw);
                }
                catch (ArgumentException ex)
                {
                    throw new EsdException($"ESD name at {nameOff} is not UTF-16: {ex.Message}");
                }
            }

            var stateIndex = new Dictionary<long, (long Group, long State)>();
            var groups = new List<StateGroup>();
            for (var gi = 0; gi < groupCount; gi++)
            {
                var at = (int)Abs(InternalHeaderSize + gi * GroupSize);
                var gid = I64(Data, at);
                var statesOff = I64(Data, at + 8);
                var n = I64(Data, at + 16);
                var first = RunStart("state", statesOff, statesAt, StateSize, stateCount, n);
                var states = new List<State>();
                for (var si = 0L; si < n; si++)
                {
                    var idx = first + si;
                    var sat = (int)Abs(statesAt + idx * StateSize);
                    var sid = I64(Data, sat);
                    states.Add(new State(
                        sid,
                        ReadConditions(I64(Data, sat + 8), I64(Data, sat + 16), []),
                        ReadCalls(I64(Data, sat + 24), I64(Data, sat + 32)),
                        ReadCalls(I64(Data, sat + 40), I64(Data, sat + 48)),
                        ReadCalls(I64(Data, sat + 56), I64(Data, sat + 64)),
                        (int)idx));
                    stateIndex[statesAt + idx * StateSize] = (gid, sid);
                }

                groups.Add(new StateGroup(gid, states, gi));
            }

            // A target may name a state parsed after its own condition, so offsets resolve only once every state's
            // position is known.
            var resolved = groups.Select(g => g with
            {
                States = g.States.Select(s => s with { Conditions = Resolve(g.Id, s.Conditions, stateIndex) }).ToList(),
            }).ToList();
            return new EsdFile(resolved, name, unk);
        }

        private static List<Condition> Resolve(long groupId, IReadOnlyList<Condition> conditions, Dictionary<long, (long Group, long State)> stateIndex)
        {
            var result = new List<Condition>();
            foreach (var c in conditions)
            {
                long? target = null;
                if (c.Target is { } offset and not -1)
                {
                    if (!stateIndex.TryGetValue(offset, out var owner))
                        throw new EsdException($"condition jump target {c.Target} does not name a state");
                    if (owner.Group != groupId)
                        throw new EsdException($"condition in group {groupId} jumps to group {owner.Group} -- cross-group jumps are unsupported, and none exist in any shipped file, so this is more likely a misparse");
                    target = owner.State;
                }

                result.Add(c with { Target = target, Subconditions = Resolve(groupId, c.Subconditions, stateIndex) });
            }

            return result;
        }

        private CommandArg[] ReadArgs(long offset, long count)
        {
            // Gated on the count: some writers use -1 for an empty list and others a live cursor.
            var first = RunStart("command arg", offset, argsAt, CommandArgSize, argCount, count);
            var result = new CommandArg[Math.Max(count, 0)];
            for (var i = 0; i < result.Length; i++)
            {
                var idx = first + i;
                var at = (int)Abs(argsAt + idx * CommandArgSize);
                var bOff = I64(Data, at);
                result[i] = new CommandArg(Blob(bOff, I64(Data, at + 8), "command arg bytecode"), (int)idx, bOff);
            }

            return result;
        }

        private CommandCall[] ReadCalls(long offset, long count)
        {
            var first = RunStart("command call", offset, callsAt, CommandCallSize, callCount, count);
            var result = new CommandCall[Math.Max(count, 0)];
            for (var i = 0; i < result.Length; i++)
            {
                var idx = first + i;
                var at = (int)Abs(callsAt + idx * CommandCallSize);
                result[i] = new CommandCall(
                    BinaryPrimitives.ReadInt32LittleEndian(Data[at..]), BinaryPrimitives.ReadInt32LittleEndian(Data[(at + 4)..]),
                    ReadArgs(I64(Data, at + 8), I64(Data, at + 16)), (int)idx);
            }

            return result;
        }

        private Condition[] ReadConditions(long poolOffset, long count, List<long> ancestors)
        {
            if (count <= 0) return [];
            // Bounded against the buffer, not the declared slot count: vanilla over-declares that.
            if (poolOffset < poolAt || (poolOffset - poolAt) % 8 != 0 || poolOffset > dataLen || count > (dataLen - poolOffset) / 8)
                throw new EsdException($"condition pool run of {count} at {poolOffset} is not inside the pool at {poolAt}");
            if (ancestors.Count >= MaxConditionDepth)
                throw new EsdException($"condition nesting is deeper than {MaxConditionDepth}");

            var result = new Condition[count];
            for (var i = 0; i < count; i++)
            {
                var condRel = I64(Data, (int)Abs(poolOffset + i * 8));
                var idx = RunStart("condition", condRel, condsAt, ConditionSize, condCount, 1);
                // Ancestors only, never every row seen: shipped files point several pools at one condition row. A
                // row reached from its own subcondition pool is the case nothing else stops.
                if (ancestors.Contains(idx))
                    throw new EsdException("condition cycle: " + string.Join(" -> ", ancestors.Append(idx)));
                var at = (int)Abs(condsAt + idx * ConditionSize);
                var evOff = I64(Data, at + 40);
                ancestors.Add(idx);
                result[i] = new Condition(
                    I64(Data, at), // still an offset; resolved once every state's position is known
                    ReadCalls(I64(Data, at + 8), I64(Data, at + 16)),
                    ReadConditions(I64(Data, at + 24), I64(Data, at + 32), ancestors),
                    Blob(evOff, I64(Data, at + 48), "condition evaluator"),
                    (int)idx, evOff);
                ancestors.RemoveAt(ancestors.Count - 1);
            }

            return result;
        }
    }

    // ---------------------------------------------------------------- ordering

    /// <summary>
    /// Source order first, then anything a merge created, in the order it was added. Reproducing a file's own layout
    /// is what makes a byte-exact round-trip possible without knowing how the original writer chose it.
    /// </summary>
    private static List<T> Ordered<T>(IEnumerable<T> records, Func<T, int?> order)
    {
        var all = records.ToList();
        var known = all.Where(r => order(r) is not null).OrderBy(order);
        return known.Concat(all.Where(r => order(r) is null)).ToList();
    }

    /// <summary>
    /// The state a group's header points at, where the machine starts: the lowest-order row. One rule in one place,
    /// because the writer and anything walking the graph must agree on it. Tuple order agrees for a group that came
    /// off disk and can differ for one a merge assembled.
    /// </summary>
    /// <exception cref="EsdException">The group has no states.</exception>
    public static State FirstState(StateGroup group)
    {
        if (group.States.Count == 0)
            throw new EsdException($"state group {group.Id} has no states -- a machine with no entry point cannot be written or walked");
        return Ordered(group.States, s => s.Order)[0];
    }

    /// <summary>
    /// Collapses repeated references to one physically shared record into a single row. A shared row is re-read fresh
    /// at each reference, so a read hands back a distinct object per reference, all carrying the row's order; order
    /// is what tells a genuine duplicate from two distinct records. Records with no order are never merged with each
    /// other. Order is only unique within the file it came from, so two records at one order that differ in content
    /// are a merge that forgot to clear a grafted record's order, and keeping either would be quietly wrong.
    /// </summary>
    private static List<T> Dedupe<T>(List<T> occurrences, Func<T, int?> order, Func<T, T, bool> same) where T : class
    {
        var seen = new Dictionary<int, T>();
        var rows = new List<T>();
        var fresh = new HashSet<T>(ReferenceEqualityComparer.Instance);
        foreach (var r in occurrences)
        {
            if (order(r) is not { } o)
            {
                if (fresh.Add(r)) rows.Add(r);
            }
            else if (!seen.TryGetValue(o, out var first))
            {
                seen[o] = r;
                rows.Add(r);
            }
            else if (!same(first, r))
            {
                throw new EsdException(CollisionMessage(o));
            }
        }

        return rows;
    }

    private static string CollisionMessage(int index) =>
        $"two different records share table index {index} -- likely a merge of two files whose order numbering collided; grafted records must carry a null order";

    private static bool Same(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

    private static bool Same(CommandArg a, CommandArg b) =>
        Same(a.Bytecode, b.Bytecode) && a.Order == b.Order && a.BlobOrder == b.BlobOrder;

    private static bool Same(CommandCall a, CommandCall b) =>
        a.Bank == b.Bank && a.Id == b.Id && a.Order == b.Order && a.Args.Count == b.Args.Count && a.Args.Zip(b.Args).All(p => Same(p.First, p.Second));

    private static bool Same(Condition a, Condition b) =>
        a.Target == b.Target && Same(a.Evaluator, b.Evaluator) && a.Order == b.Order && a.BlobOrder == b.BlobOrder
        && a.PassCommands.Count == b.PassCommands.Count && a.PassCommands.Zip(b.PassCommands).All(p => Same(p.First, p.Second))
        && a.Subconditions.Count == b.Subconditions.Count && a.Subconditions.Zip(b.Subconditions).All(p => Same(p.First, p.Second));

    // ---------------------------------------------------------------- write

    /// <summary>Serialises an ESD. Reproduces a file the reader read, byte for byte.</summary>
    /// <exception cref="EsdException">The graph cannot be laid out: an unknown header that is not four words, a group with no states, a jump to a state the group lacks, or a record inserted into an existing run.</exception>
    public static byte[] Write(EsdFile archive)
    {
        if (archive.Unk.Count != 4)
            throw new EsdException($"the internal header holds four unknown words, not {archive.Unk.Count}");
        var groups = Ordered(archive.Groups, g => g.Order);
        var entry = groups.ToDictionary(g => g.Id, FirstState);

        // Every reference is kept, including repeats of a shared record, so the tables see the full picture and any
        // occurrence a nested walk hands back can be answered for. A condition's group is recorded as it is visited:
        // a shared condition's target lies in the group of every state that references it (the reader rejects
        // cross-group jumps), so one answer per condition suffices.
        List<Condition> allConditions = [];
        List<CommandCall> allCalls = [];
        List<CommandArg> allArgs = [];
        var condGroup = new Dictionary<Condition, long>(ReferenceEqualityComparer.Instance);

        void CollectCalls(IEnumerable<CommandCall> calls)
        {
            foreach (var call in calls)
            {
                allCalls.Add(call);
                allArgs.AddRange(call.Args);
            }
        }

        void CollectConditions(IEnumerable<Condition> conditions, long groupId)
        {
            foreach (var cond in conditions)
            {
                allConditions.Add(cond);
                condGroup[cond] = groupId;
                CollectCalls(cond.PassCommands);
                CollectConditions(cond.Subconditions, groupId);
            }
        }

        foreach (var group in groups)
        {
            foreach (var state in Ordered(group.States, s => s.Order))
            {
                CollectConditions(state.Conditions, group.Id);
                CollectCalls(state.Entry);
                CollectCalls(state.Exit);
                CollectCalls(state.While);
            }
        }

        var calls = Ordered(Dedupe(allCalls, c => c.Order, Same), c => c.Order);
        var args = Ordered(Dedupe(allArgs, a => a.Order, Same), a => a.Order);
        var conditions = Ordered(Dedupe(allConditions, c => c.Order, Same), c => c.Order);
        var callSlot = Slots(allCalls, calls, c => c.Order);
        var argSlot = Slots(allArgs, args, a => a.Order);
        var condSlot = Slots(allConditions, conditions, c => c.Order);

        // State table: each group's states, then one dummy copy of its entry state when it holds more than one.
        // Position is what a jump target names, so this is fixed before anything that references a state is emitted.
        var stateSlot = new Dictionary<(long, long), int>();
        var stateRows = new List<(long Group, State State)>();
        foreach (var group in groups)
        {
            var ordered = Ordered(group.States, s => s.Order);
            foreach (var state in ordered)
            {
                stateSlot[(group.Id, state.Id)] = stateRows.Count;
                stateRows.Add((group.Id, state));
            }

            if (ordered.Count > 1) stateRows.Add((group.Id, entry[group.Id])); // dummy
        }

        long groupCount = groups.Count;
        const long groupsAt = InternalHeaderSize;
        var statesAt = groupsAt + groupCount * GroupSize;
        var condsAt = statesAt + stateRows.Count * StateSize;
        var callsAt = condsAt + conditions.Count * ConditionSize;
        var argsAt = callsAt + calls.Count * CommandCallSize;
        var poolAt = argsAt + args.Count * CommandArgSize;

        // One pool slot per state-to-condition and condition-to-subcondition link, in graph-walk order. A state whose
        // conditions are shared with another state still reserves its own slots; only the value written is shared.
        List<long> pool = [];
        var poolStart = new Dictionary<(long, long), long>();
        var subPoolStart = new Dictionary<Condition, long>(ReferenceEqualityComparer.Instance);

        long Reserve(IEnumerable<Condition> conds)
        {
            var start = poolAt + pool.Count * 8;
            foreach (var cond in conds) pool.Add(condsAt + condSlot[cond] * ConditionSize);
            return start;
        }

        foreach (var group in groups)
        {
            foreach (var state in Ordered(group.States, s => s.Order))
                poolStart[(group.Id, state.Id)] = Reserve(state.Conditions);
        }

        foreach (var cond in conditions)
        {
            if (cond.Subconditions.Count > 0) subPoolStart[cond] = Reserve(cond.Subconditions);
        }

        var blobsAt = poolAt + pool.Count * 8;

        // Payloads in source order, so an untouched file's region comes back identical. Rows are already deduplicated,
        // so a shared record's payload is written once.
        var blobs = new List<(long? Order, byte[] Payload, object Owner)>();
        blobs.AddRange(conditions.Where(c => c.Evaluator.Length > 0).Select(c => (c.BlobOrder, c.Evaluator, (object)c)));
        blobs.AddRange(args.Where(a => a.Bytecode.Length > 0).Select(a => (a.BlobOrder, a.Bytecode, (object)a)));
        var blobAt = new Dictionary<object, long>(ReferenceEqualityComparer.Instance);
        var blobBytes = new MemoryStream();
        foreach (var (_, payload, owner) in blobs.Where(b => b.Order is not null).OrderBy(b => b.Order).Concat(blobs.Where(b => b.Order is null)))
        {
            blobAt[owner] = blobsAt + blobBytes.Length;
            blobBytes.Write(payload);
        }

        var nameAt = blobsAt + blobBytes.Length;
        var nameLen = archive.Name.Length > 0 ? archive.Name.Length + 1 : 0;
        var dataEnd = nameAt + nameLen * 2L;

        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Magic.ToArray());
        w.Write(1u); w.Write(3u); w.Write(3u); w.Write(0x54u); w.Write((uint)dataEnd);
        w.Write(6u);
        foreach (var (size, count) in new (int, int)[]
                 {
                     (InternalHeaderSize, 1), (GroupSize, (int)groupCount), (StateSize, stateRows.Count),
                     (ConditionSize, conditions.Count), (CommandCallSize, calls.Count), (CommandArgSize, args.Count),
                 })
        {
            w.Write((uint)size);
            w.Write((uint)count);
        }

        w.Write((uint)poolAt); w.Write((uint)pool.Count); w.Write((uint)nameAt); w.Write((uint)nameLen);
        w.Write((uint)dataEnd); w.Write(0u); w.Write((uint)dataEnd); w.Write(0u);

        w.Write(1u);
        foreach (var u in archive.Unk) w.Write(u);
        w.Write(0u);
        w.Write(groupsAt); w.Write(groupCount); w.Write(nameLen > 0 ? nameAt : -1L); w.Write((long)nameLen); w.Write(-1L); w.Write(-1L);

        foreach (var group in groups)
        {
            var at = statesAt + stateSlot[(group.Id, entry[group.Id].Id)] * (long)StateSize;
            w.Write(group.Id); w.Write(at); w.Write((long)group.States.Count); w.Write(at);
        }

        // A run is addressed as (first slot, count) and the reader recovers the rest by walking forward, which only
        // works if the run is contiguous in the rewritten table. A merge that appends a call to an existing owner's
        // list lands it at the tail with the other fresh records instead, and trusting the first slot would hand
        // back whichever call sits in the neighbouring row. Refuse instead.
        (long Offset, long Count) CallSpan(IReadOnlyList<CommandCall> items, string owner)
        {
            if (items.Count == 0) return (-1, 0);
            var first = callSlot[items[0]];
            for (var i = 0; i < items.Count; i++)
            {
                if (callSlot[items[i]] != first + i)
                    throw new EsdException($"{owner} does not occupy a contiguous run in the rewritten call table -- inserting into an existing owner's list without relaying out the whole table is not supported");
            }

            return (callsAt + first * (long)CommandCallSize, items.Count);
        }

        foreach (var (groupId, state) in stateRows)
        {
            // A zero-condition state still points at wherever the pool cursor stood, never at -1; real mod files
            // confirm it, and -1 would break the byte-exact round trip.
            var coOff = poolStart[(groupId, state.Id)];
            var (enOff, enN) = CallSpan(state.Entry, $"state {groupId}/{state.Id} entry");
            var (exOff, exN) = CallSpan(state.Exit, $"state {groupId}/{state.Id} exit");
            var (whOff, whN) = CallSpan(state.While, $"state {groupId}/{state.Id} while");
            w.Write(state.Id); w.Write(coOff); w.Write((long)state.Conditions.Count);
            w.Write(enOff); w.Write(enN); w.Write(exOff); w.Write(exN); w.Write(whOff); w.Write(whN);
        }

        foreach (var cond in conditions)
        {
            long target = -1;
            if (cond.Target is { } wanted)
            {
                var owner = condGroup[cond];
                if (!stateSlot.TryGetValue((owner, wanted), out var slot))
                    throw new EsdException($"condition in group {owner} jumps to state {wanted}, which that group does not have -- a merge left a jump target behind when it renumbered");
                target = statesAt + slot * (long)StateSize;
            }

            var (pcOff, pcN) = CallSpan(cond.PassCommands, $"condition (order={cond.Order}) pass_commands");
            var scOff = cond.Subconditions.Count > 0 ? subPoolStart[cond] : -1;
            var evOff = cond.Evaluator.Length > 0 ? blobAt[cond] : -1;
            w.Write(target); w.Write(pcOff); w.Write(pcN); w.Write(scOff); w.Write((long)cond.Subconditions.Count);
            w.Write(evOff); w.Write((long)cond.Evaluator.Length);
        }

        // A call's arg-run offset is unconditional for the same reason a state's pool offset is: the table is one
        // flat run per call, back to back in call order, so a zero-arg call still gets the cursor rather than -1.
        // The offset is a running cursor and not a lookup, so check the args actually landed where it says; an arg
        // added to an existing call would sit at the tail of the table instead.
        var argsCursor = 0;
        foreach (var call in calls)
        {
            for (var i = 0; i < call.Args.Count; i++)
            {
                if (argSlot[call.Args[i]] != argsCursor + i)
                    throw new EsdException($"command call (bank={call.Bank}, id={call.Id}, order={call.Order}) does not occupy a contiguous run in the rewritten arg table -- inserting into an existing call's arg list without relaying out the whole table is not supported");
            }

            w.Write(call.Bank); w.Write(call.Id); w.Write(argsAt + argsCursor * (long)CommandArgSize); w.Write((long)call.Args.Count);
            argsCursor += call.Args.Count;
        }

        foreach (var arg in args)
        {
            w.Write(arg.Bytecode.Length > 0 ? blobAt[arg] : -1L);
            w.Write((long)arg.Bytecode.Length);
        }

        foreach (var slot in pool) w.Write(slot);
        w.Write(blobBytes.ToArray());
        if (nameLen > 0)
        {
            w.Write(Utf16.GetBytes(archive.Name));
            w.Write((ushort)0);
        }

        return ms.ToArray();
    }

    private static Dictionary<T, int> Slots<T>(List<T> occurrences, List<T> rows, Func<T, int?> order) where T : class
    {
        var byOrder = new Dictionary<int, int>();
        var byRef = new Dictionary<T, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < rows.Count; i++)
        {
            if (order(rows[i]) is { } o) byOrder[o] = i;
            else byRef[rows[i]] = i;
        }

        var result = new Dictionary<T, int>(ReferenceEqualityComparer.Instance);
        foreach (var r in occurrences)
            result[r] = order(r) is { } k ? byOrder[k] : byRef[r];
        return result;
    }

    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    private static long I64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);
}
