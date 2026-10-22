using System.Buffers.Binary;

namespace NexusMods.Games.FromSoft.Merge;

/// <summary>A decoded condition evaluator or command argument, comparable by structure.</summary>
public abstract record EsdExpr;

public sealed record EsdInt(int Value) : EsdExpr;

public sealed record EsdFloat(double Value) : EsdExpr;

/// <summary>A call: the function id first, then its arguments.</summary>
public sealed record EsdCall(EsdList<EsdExpr> Operands) : EsdExpr;

public sealed record EsdBuiltin(byte Op, EsdList<EsdExpr> Args) : EsdExpr;

public sealed record EsdBinary(byte Op, EsdExpr Lhs, EsdExpr Rhs) : EsdExpr;

/// <summary>A flattened run of one associative operator (&amp;&amp; or ||), so how it was grouped is not part of what it means.</summary>
public sealed record EsdChain(byte Op, EsdList<EsdExpr> Operands) : EsdExpr;

/// <summary>
/// Decodes the postfix stack programs behind condition evaluators and command arguments. Three encoders wrote the
/// files a merge has to compare (FromSoft's compiler and the toolchain behind each mod) and they agree on the
/// machine while disagreeing on nearly every spelling of it, so an edit can only be told from a re-encode by
/// decoding both sides and comparing the expressions.
/// </summary>
/// <remarks>
/// The opcode table is not documented anywhere. It was recovered by decoding every blob in the real files and
/// requiring each to consume its bytes exactly and leave exactly one value on the stack at its terminator; a table
/// that is off anywhere fails hundreds of blobs. Anything outside it is refused, never guessed at.
/// </remarks>
public static class EsdExpressions
{
    private const byte End = 0xA1;
    private const byte FloatImmediate = 0x80, DoubleImmediate = 0x81, IntImmediate = 0x82;
    private const byte FirstCall = 0x84, LastCall = 0x8B;        // pops (op - 0x83) values: function id, then args
    private const byte FirstBinary = 0x8C, LastBinary = 0x9B;
    private const byte MarkerA = 0xA6, MarkerB = 0xB7;           // FromSoft-only; leave the value stack untouched
    private const byte FirstSave = 0xA7, LastSave = 0xAE;        // cache the top value in slot (op - 0xA7)
    private const byte FirstLoad = 0xAF, LastLoad = 0xB6;        // push slot (op - 0xAF) back on
    private const byte OneArgBuiltin = 0xB8;
    private const byte NoArgBuiltinA = 0xB9, NoArgBuiltinB = 0xBA;

    public const byte Equal = 0x95, NotEqual = 0x96, And = 0x98, Or = 0x99;

    private static readonly EsdInt True = new(1);

    /// <summary>
    /// Decodes one blob. <paramref name="slots"/> is the register file shared across everything in one state: the
    /// compiler computes a subexpression once, parks it, and reloads it from a later condition of the same state.
    /// </summary>
    /// <exception cref="EsdMergeException">The blob is not one well-formed expression.</exception>
    public static EsdExpr Decode(ReadOnlySpan<byte> bytecode, IDictionary<int, EsdExpr> slots)
    {
        var stack = new List<EsdExpr>();
        var at = 0;
        while (at < bytecode.Length)
        {
            var op = bytecode[at++];
            if (op == End)
            {
                if (at != bytecode.Length || stack.Count != 1)
                {
                    throw new EsdMergeException(
                        $"bytecode {Convert.ToHexString(bytecode)} is not one expression -- it ends with {stack.Count} values on the stack");
                }

                return stack[0];
            }

            if (op <= 0x7F)
            {
                stack.Add(new EsdInt(op - 0x40)); // 0x40 is zero; 0x3F is -1
            }
            else if (op is FloatImmediate or DoubleImmediate or IntImmediate)
            {
                var size = op == DoubleImmediate ? 8 : 4;
                if (at + size > bytecode.Length)
                    throw new EsdMergeException($"bytecode {Convert.ToHexString(bytecode)} is cut off mid-literal");
                var body = bytecode.Slice(at, size);
                stack.Add(op switch
                {
                    IntImmediate => new EsdInt(BinaryPrimitives.ReadInt32LittleEndian(body)),
                    FloatImmediate => new EsdFloat(BinaryPrimitives.ReadSingleLittleEndian(body)),
                    _ => new EsdFloat(BinaryPrimitives.ReadDoubleLittleEndian(body)),
                });
                at += size;
            }
            else if (op is >= FirstCall and <= LastCall)
            {
                stack.Add(new EsdCall(new EsdList<EsdExpr>(Take(stack, op - 0x83, bytecode))));
            }
            else if (op is >= FirstBinary and <= LastBinary)
            {
                var operands = Take(stack, 2, bytecode);
                stack.Add(Combine(op, operands[0], operands[1]));
            }
            else if (op == OneArgBuiltin)
            {
                stack.Add(new EsdBuiltin(op, new EsdList<EsdExpr>(Take(stack, 1, bytecode))));
            }
            else if (op is NoArgBuiltinA or NoArgBuiltinB)
            {
                stack.Add(new EsdBuiltin(op, EsdList<EsdExpr>.Empty));
            }
            else if (op is MarkerA or MarkerB)
            {
                // No effect on the value stack, so no part of what the expression means.
            }
            else if (op is >= FirstSave and <= LastSave)
            {
                var cached = Take(stack, 1, bytecode)[0];
                slots[op - FirstSave] = cached;
                stack.Add(cached); // saving leaves the value where it was
            }
            else if (op is >= FirstLoad and <= LastLoad)
            {
                var slot = op - FirstLoad;
                if (!slots.TryGetValue(slot, out var cached))
                {
                    throw new EsdMergeException(
                        $"bytecode {Convert.ToHexString(bytecode)} reads cache slot {slot} before anything in this state wrote it");
                }

                stack.Add(cached);
            }
            else
            {
                throw new EsdMergeException(
                    $"bytecode {Convert.ToHexString(bytecode)} uses opcode 0x{op:x2}, which this decoder has never seen -- refusing to guess what it means");
            }
        }

        throw new EsdMergeException($"bytecode {Convert.ToHexString(bytecode)} has no terminator");
    }

    private static EsdExpr[] Take(List<EsdExpr> stack, int count, ReadOnlySpan<byte> bytecode)
    {
        if (stack.Count < count)
        {
            throw new EsdMergeException(
                $"bytecode {Convert.ToHexString(bytecode)} pops {count} values off a stack of {stack.Count}");
        }

        var taken = stack.GetRange(stack.Count - count, count).ToArray();
        stack.RemoveRange(stack.Count - count, count);
        return taken;
    }

    /// <summary>Builds a binary node, folding the spellings the encoders disagree about.</summary>
    public static EsdExpr Combine(byte op, EsdExpr lhs, EsdExpr rhs)
    {
        if (op is Equal or NotEqual && IsLiteral(lhs) && !IsLiteral(rhs))
            (lhs, rhs) = (rhs, lhs); // the constant goes on the right
        if (op == Equal && rhs == True) return lhs; // `X == 1` is `X`
        if (op == And && rhs == True) return lhs;
        if (op == And && lhs == True) return rhs;
        if (op is And or Or)
        {
            // Associative, so chains stay flat: a run of same-target branches merges into one of these and the
            // grouping must not matter.
            return new EsdChain(op, new EsdList<EsdExpr>(Operands(op, lhs).Concat(Operands(op, rhs))));
        }

        return new EsdBinary(op, lhs, rhs);
    }

    private static bool IsLiteral(EsdExpr node) => node is EsdInt or EsdFloat;

    private static IEnumerable<EsdExpr> Operands(byte op, EsdExpr node) =>
        node is EsdChain chain && chain.Op == op ? chain.Operands : [node];
}
