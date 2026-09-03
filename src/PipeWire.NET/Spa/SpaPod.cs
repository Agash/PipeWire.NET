using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;

namespace PipeWire.NET.Spa;

/// <summary>
/// Reads and writes SPA POD wire format as <see cref="SpaValue"/> trees.
/// </summary>
/// <remarks>
/// <para>
/// The wire format is <c>[uint32 size][uint32 type][body]</c>, every pod padded to eight bytes.
/// Sizes on the wire come from the daemon and are treated as hostile throughout: a size larger than
/// the buffer, a negative remainder, or a nesting depth chosen to exhaust the stack all end as a
/// failed parse rather than an exception.
/// </para>
/// <para>
/// This is the whole-tree API, for parameters whose shape is not known ahead of time. Code that
/// knows exactly which fields it wants - stream format negotiation, for instance - uses the
/// internal reader and builder directly and never allocates a tree.
/// </para>
/// <para>
/// A note on byte order. Pods are native-endian: the protocol is a unix socket between processes
/// on one machine, and PipeWire swaps nothing anywhere - checked against its own connection.c
/// and pod headers, which memcpy structs and read fields directly. Every integer access here
/// therefore goes through <see cref="MemoryMarshal"/> reads and writes, which are native-endian
/// by construction, rather than spelling out an order. On today's little-endian targets the
/// bytes are identical either way; on a big-endian host this is the half that stays right.
/// </para>
/// </remarks>
public static class SpaPod
{
    /// <summary>
    /// How deeply values may nest before a pod is rejected.
    /// </summary>
    /// <remarks>
    /// Real parameters nest three or four deep. The limit exists so a pod crafted to nest thousands
    /// of times fails to parse instead of overflowing the stack, which no <c>catch</c> can recover.
    /// </remarks>
    public const int MaxDepth = 32;

    /// <summary>Parses one pod, including everything nested inside it.</summary>
    /// <param name="pod">The bytes, starting at the pod header.</param>
    /// <param name="value">The decoded value, or <see langword="null"/> when parsing failed.</param>
    /// <returns><see langword="false"/> if the bytes are not a well-formed pod.</returns>
    public static bool TryParse(ReadOnlySpan<byte> pod, out SpaValue? value)
    {
        value = null;
        return TryParseValue(pod, 0, out value, out _) && value is not null;
    }

    /// <summary>The number of bytes <see cref="TryWrite"/> needs, padding included.</summary>
    /// <param name="value">The value to measure.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static int GetByteCount(SpaValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return checked(Pad(8 + BodySize(value)));
    }

    /// <summary>Writes a value in wire format.</summary>
    /// <param name="value">The value to write.</param>
    /// <param name="destination">Where to write it; must be at least <see cref="GetByteCount"/> long.</param>
    /// <param name="written">How many bytes were written, padding included.</param>
    /// <returns><see langword="false"/> if <paramref name="destination"/> was too short.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static bool TryWrite(SpaValue value, Span<byte> destination, out int written)
    {
        ArgumentNullException.ThrowIfNull(value);

        written = 0;
        int needed = GetByteCount(value);
        if (destination.Length < needed)
            return false;

        destination[..needed].Clear();
        WriteValue(value, destination);
        written = needed;
        return true;
    }

    /// <summary>Writes a value in wire format into a new array.</summary>
    /// <param name="value">The value to write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static byte[] ToBytes(SpaValue value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var buffer = new byte[GetByteCount(value)];
        TryWrite(value, buffer, out _);
        return buffer;
    }

    // - Parsing -

    private static bool TryParseValue(ReadOnlySpan<byte> pod, int depth, out SpaValue? value, out int consumed)
    {
        value = null;
        consumed = 0;

        if (depth > MaxDepth || pod.Length < 8)
            return false;

        uint size = MemoryMarshal.Read<uint>(pod);
        var type = (SpaType)MemoryMarshal.Read<uint>(pod[4..]);

        // The declared size is the daemon's word against the buffer we actually hold.
        if (size > (uint)(pod.Length - 8))
            return false;

        ReadOnlySpan<byte> body = pod.Slice(8, (int)size);
        if (!TryParseBody(type, body, depth, out value))
            return false;

        // Pods are padded to eight bytes when another follows. The last one in a buffer often is
        // not, so an unpadded tail is accepted too rather than rejecting a well-formed final pod.
        int unpadded = 8 + (int)size;
        int padded = Pad(unpadded);

        if (padded <= pod.Length) { consumed = padded; return true; }
        if (unpadded <= pod.Length) { consumed = unpadded; return true; }
        return false;
    }

    private static bool TryParseBody(SpaType type, ReadOnlySpan<byte> body, int depth, out SpaValue? value)
    {
        value = null;

        switch (type)
        {
            case SpaType.None:
                value = SpaNone.Instance;
                return true;

            case SpaType.Bool:
                if (body.Length < 4) return false;
                value = new SpaBool(MemoryMarshal.Read<int>(body) != 0);
                return true;

            case SpaType.Id:
                if (body.Length < 4) return false;
                value = new SpaId(MemoryMarshal.Read<uint>(body));
                return true;

            case SpaType.Int:
                if (body.Length < 4) return false;
                value = new SpaInt(MemoryMarshal.Read<int>(body));
                return true;

            case SpaType.Long:
                if (body.Length < 8) return false;
                value = new SpaLong(MemoryMarshal.Read<long>(body));
                return true;

            case SpaType.Float:
                if (body.Length < 4) return false;
                value = new SpaFloat(MemoryMarshal.Read<float>(body));
                return true;

            case SpaType.Double:
                if (body.Length < 8) return false;
                value = new SpaDouble(MemoryMarshal.Read<double>(body));
                return true;

            case SpaType.String:
            {
                // Cut at the first NUL, not just the last byte. The size counts the terminator, but
                // a padded or fixed-length producer can send several - trimming one then leaves
                // embedded NULs in a C# string, which breaks equality and every later comparison.
                int end = body.IndexOf((byte)0);
                ReadOnlySpan<byte> text = end >= 0 ? body[..end] : body;
                value = new SpaString(Encoding.UTF8.GetString(text));
                return true;
            }

            case SpaType.Bytes:
                value = new SpaBytes([.. body]);
                return true;

            case SpaType.Rectangle:
                if (body.Length < 8) return false;
                value = new SpaRectangle(
                    MemoryMarshal.Read<uint>(body),
                    MemoryMarshal.Read<uint>(body[4..]));
                return true;

            case SpaType.Fraction:
                if (body.Length < 8) return false;
                value = new SpaFraction(
                    MemoryMarshal.Read<uint>(body),
                    MemoryMarshal.Read<uint>(body[4..]));
                return true;

            case SpaType.Bitmap:
                value = new SpaBitmap([.. body]);
                return true;

            case SpaType.Fd:
                if (body.Length < 8) return false;
                value = new SpaFd(MemoryMarshal.Read<long>(body));
                return true;

            case SpaType.Pointer:
            {
                // [uint32 type][uint32 padding][pointer], the pointer being native-word sized.
                if (body.Length < 8 + IntPtr.Size) return false;
                var pointerType = (SpaType)MemoryMarshal.Read<uint>(body);
                ulong address = IntPtr.Size == 8
                    ? MemoryMarshal.Read<ulong>(body[8..])
                    : MemoryMarshal.Read<uint>(body[8..]);
                value = new SpaPointer(pointerType, address);
                return true;
            }

            case SpaType.Array:
                return TryParseArray(body, depth, out value);

            case SpaType.Struct:
                return TryParseStruct(body, depth, out value);

            case SpaType.Object:
                return TryParseObject(body, depth, out value);

            case SpaType.Choice:
                return TryParseChoice(body, depth, out value);

            case SpaType.Sequence:
                return TryParseSequence(body, depth, out value);

            case SpaType.Pod:
            {
                if (!TryParseValue(body, depth + 1, out SpaValue? inner, out _) || inner is null)
                    return false;
                value = new SpaNestedPod(inner);
                return true;
            }

            default:
                // Deliberately not a failure: a newer daemon may send a type this version predates,
                // and rejecting it would discard the whole enclosing object.
                value = new SpaUnknown(type, [.. body]);
                return true;
        }
    }

    private static bool TryParseArray(ReadOnlySpan<byte> body, int depth, out SpaValue? value)
    {
        value = null;
        if (body.Length < 8) return false;

        uint childSize = MemoryMarshal.Read<uint>(body);
        var childType = (SpaType)MemoryMarshal.Read<uint>(body[4..]);
        ReadOnlySpan<byte> items = body[8..];

        // A zero child size with a non-empty body would loop forever; an empty array is legal.
        if (childSize == 0)
        {
            value = items.IsEmpty
                ? new SpaArray(childType, [])
                : null;
            return items.IsEmpty;
        }

        // childSize is the producer's word and it is unsigned. Casting it first turns anything above
        // int.MaxValue negative, which makes the item count negative, and the builder is then asked
        // for a negative capacity: an exception out of a parser whose whole contract is to return
        // false. Nothing legitimate is anywhere near this large.
        if (childSize > int.MaxValue || childSize > (uint)items.Length) return false;
        if (!ChildrenTileExactly(childSize, childType, items.Length)) return false;

        int count = items.Length / (int)childSize;

        var builder = ImmutableArray.CreateBuilder<SpaValue>(count);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> item = items.Slice(i * (int)childSize, (int)childSize);
            if (!TryParseBody(childType, item, depth + 1, out SpaValue? child) || child is null)
                return false;
            builder.Add(child);
        }

        value = new SpaArray(childType, builder.MoveToImmutable());
        return true;
    }

    /// <summary>Whether a bare-child body of that many bytes is a whole number of well-sized children.</summary>
    /// <remarks>
    /// Two separate lies get caught here. A body that is not a whole number of children means bytes
    /// the producer described as nothing, and dividing them away parses a prefix and reports
    /// success. A child size that disagrees with the child type means every child fails to parse,
    /// but only after a builder has been sized from the count the wrong size implied - one entry per
    /// byte for a one-byte Int.
    /// </remarks>
    private static bool ChildrenTileExactly(uint childSize, SpaType childType, int itemsLength)
    {
        if ((uint)itemsLength % childSize != 0) return false;

        int fixedSize = FixedBodySize(childType);
        return fixedSize < 0 || childSize == (uint)fixedSize;
    }

    private static bool TryParseStruct(ReadOnlySpan<byte> body, int depth, out SpaValue? value)
    {
        value = null;
        var fields = ImmutableArray.CreateBuilder<SpaValue>();

        int offset = 0;
        while (offset + 8 <= body.Length)
        {
            if (!TryParseValue(body[offset..], depth + 1, out SpaValue? field, out int consumed) || field is null)
                return false;
            if (consumed <= 0) return false;
            fields.Add(field);
            offset += consumed;
        }

        // Same rule as an object: the loop stops when fewer than a header remains, so a remainder is
        // a body that ended mid-field. Accepting it dropped the tail and reported success, which
        // reads downstream as a producer that simply sent fewer fields.
        if (offset != body.Length) return false;

        value = new SpaStruct(fields.ToImmutable());
        return true;
    }

    private static bool TryParseObject(ReadOnlySpan<byte> body, int depth, out SpaValue? value)
    {
        value = null;
        if (body.Length < 8) return false;

        var objectType = (SpaType)MemoryMarshal.Read<uint>(body);
        var objectId = (SpaParamType)MemoryMarshal.Read<uint>(body[4..]);

        var properties = ImmutableArray.CreateBuilder<SpaProperty>();
        int offset = 8;
        while (offset + 16 <= body.Length)
        {
            uint key = MemoryMarshal.Read<uint>(body[offset..]);
            uint flags = MemoryMarshal.Read<uint>(body[(offset + 4)..]);

            if (!TryParseValue(body[(offset + 8)..], depth + 1, out SpaValue? propertyValue, out int consumed)
                || propertyValue is null || consumed <= 0)
            {
                return false;
            }

            properties.Add(new SpaProperty(key, flags, propertyValue));
            offset += 8 + consumed;
        }

        // The loop stops as soon as fewer than a property header remains, so anything left over is
        // a body that ended mid-property. Accepting it returned an object missing its last property
        // and reported success, which reads downstream as a producer that simply did not offer it.
        if (offset != body.Length) return false;

        value = new SpaObject(objectType, objectId, properties.ToImmutable());
        return true;
    }

    private static bool TryParseChoice(ReadOnlySpan<byte> body, int depth, out SpaValue? value)
    {
        value = null;
        if (body.Length < 16) return false;

        var kind = (SpaChoiceType)MemoryMarshal.Read<uint>(body);
        uint childSize = MemoryMarshal.Read<uint>(body[8..]);
        var childType = (SpaType)MemoryMarshal.Read<uint>(body[12..]);
        ReadOnlySpan<byte> items = body[16..];

        if (childSize == 0)
        {
            if (!items.IsEmpty) return false;
            value = new SpaChoice(kind, childType, []);
            return true;
        }

        // childSize is the producer's word and it is unsigned. Casting it first turns anything above
        // int.MaxValue negative, which makes the item count negative, and the builder is then asked
        // for a negative capacity: an exception out of a parser whose whole contract is to return
        // false. Nothing legitimate is anywhere near this large.
        if (childSize > int.MaxValue || childSize > (uint)items.Length) return false;
        if (!ChildrenTileExactly(childSize, childType, items.Length)) return false;

        int count = items.Length / (int)childSize;
        // The kind says how to read the positions - a Range is default, min, max - so a count that
        // does not match it is a pod that cannot be interpreted, not one that is merely unusual.
        // Refused here: the record's own check throws, which is right for a caller building one by
        // hand and wrong on a parse path whose whole contract is to return false.
        if (!SpaChoice.CountFitsKind(kind, count)) return false;

        var builder = ImmutableArray.CreateBuilder<SpaValue>(count);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> item = items.Slice(i * (int)childSize, (int)childSize);
            if (!TryParseBody(childType, item, depth + 1, out SpaValue? child) || child is null)
                return false;
            builder.Add(child);
        }

        value = new SpaChoice(kind, childType, builder.MoveToImmutable());
        return true;
    }

    private static bool TryParseSequence(ReadOnlySpan<byte> body, int depth, out SpaValue? value)
    {
        value = null;
        if (body.Length < 8) return false;

        uint unit = MemoryMarshal.Read<uint>(body);
        var controls = ImmutableArray.CreateBuilder<SpaControl>();

        int offset = 8;
        while (offset + 16 <= body.Length)
        {
            uint controlOffset = MemoryMarshal.Read<uint>(body[offset..]);
            uint controlType = MemoryMarshal.Read<uint>(body[(offset + 4)..]);

            if (!TryParseValue(body[(offset + 8)..], depth + 1, out SpaValue? controlValue, out int consumed)
                || controlValue is null || consumed <= 0)
            {
                return false;
            }

            controls.Add(new SpaControl(controlOffset, controlType, controlValue));
            offset += 8 + consumed;
        }

        if (offset != body.Length) return false;

        value = new SpaSequence(unit, controls.ToImmutable());
        return true;
    }

    // - Writing -

    /// <remarks>
    /// Checked on purpose. A tree parsed from a large pod and measured for rewriting can multiply an
    /// item count by a child size past int.MaxValue, and unchecked that wraps negative - which
    /// reaches a new byte[] and a span slice as a negative length, throwing something nobody
    /// documented from a method whose whole contract is a byte count.
    /// </remarks>
    private static int BodySize(SpaValue value) => checked(value switch
    {
        SpaNone => 0,
        SpaBool or SpaId or SpaInt or SpaFloat => 4,
        SpaLong or SpaDouble or SpaFd or SpaRectangle or SpaFraction => 8,
        SpaString s => Encoding.UTF8.GetByteCount(s.Value) + 1,
        SpaBytes b => b.Value.Length,
        SpaBitmap b => b.Bits.Length,
        SpaPointer => 8 + IntPtr.Size,
        SpaArray a => 8 + (a.Items.Length * ChildSize(a.ChildType, a.Items)),
        SpaChoice c => 16 + (c.Alternatives.Length * ChildSize(c.ChildType, c.Alternatives)),
        SpaStruct s => SumPadded(s.Fields),
        SpaObject o => 8 + SumProperties(o.Properties),
        SpaSequence s => 8 + SumControls(s.Controls),
        SpaNestedPod p => Pad(8 + BodySize(p.Value)),
        SpaUnknown u => u.Body.Length,
        _ => 0,
    });

    // Array and choice children are stored bare, so every child must agree on one size. The
    // declared child type decides it; a value that disagrees is written truncated or padded to fit,
    // which is what the wire format can express.
    private static int ChildSize(SpaType childType, ImmutableArray<SpaValue> children)
    {
        int fixedSize = FixedBodySize(childType);
        if (fixedSize >= 0)
            return fixedSize;

        int max = 0;
        foreach (SpaValue child in children)
            max = Math.Max(max, BodySize(child));
        return max;
    }

    private static int FixedBodySize(SpaType type) => type switch
    {
        SpaType.None => 0,
        SpaType.Bool or SpaType.Id or SpaType.Int or SpaType.Float => 4,
        SpaType.Long or SpaType.Double or SpaType.Fd
            or SpaType.Rectangle or SpaType.Fraction => 8,
        _ => -1,
    };

    private static int SumPadded(ImmutableArray<SpaValue> values)
    {
        int total = 0;
        foreach (SpaValue value in values)
            total = checked(total + Pad(8 + BodySize(value)));
        return total;
    }

    private static int SumProperties(ImmutableArray<SpaProperty> properties)
    {
        int total = 0;
        foreach (SpaProperty property in properties)
            total = checked(total + 8 + Pad(8 + BodySize(property.Value)));
        return total;
    }

    private static int SumControls(ImmutableArray<SpaControl> controls)
    {
        int total = 0;
        foreach (SpaControl control in controls)
            total = checked(total + 8 + Pad(8 + BodySize(control.Value)));
        return total;
    }

    private static void WriteValue(SpaValue value, Span<byte> destination)
    {
        int size = BodySize(value);
        MemoryMarshal.Write<uint>(destination, (uint)size);
        MemoryMarshal.Write<uint>(destination[4..], (uint)value.Type);
        WriteBody(value, destination.Slice(8, size));
    }

    private static void WriteBody(SpaValue value, Span<byte> body)
    {
        switch (value)
        {
            case SpaNone:
                break;

            case SpaBool b:
                MemoryMarshal.Write<int>(body, b.Value ? 1 : 0);
                break;

            case SpaId id:
                MemoryMarshal.Write<uint>(body, id.Value);
                break;

            case SpaInt i:
                MemoryMarshal.Write<int>(body, i.Value);
                break;

            case SpaLong l:
                MemoryMarshal.Write<long>(body, l.Value);
                break;

            case SpaFloat f:
                MemoryMarshal.Write<float>(body, f.Value);
                break;

            case SpaDouble d:
                MemoryMarshal.Write<double>(body, d.Value);
                break;

            case SpaString s:
                // The trailing NUL is already there: the destination was cleared before writing.
                Encoding.UTF8.GetBytes(s.Value, body);
                break;

            case SpaBytes b:
                b.Value.AsSpan().CopyTo(body);
                break;

            case SpaBitmap b:
                b.Bits.AsSpan().CopyTo(body);
                break;

            case SpaRectangle r:
                MemoryMarshal.Write<uint>(body, r.Width);
                MemoryMarshal.Write<uint>(body[4..], r.Height);
                break;

            case SpaFraction f:
                MemoryMarshal.Write<uint>(body, f.Numerator);
                MemoryMarshal.Write<uint>(body[4..], f.Denominator);
                break;

            case SpaFd fd:
                MemoryMarshal.Write<long>(body, fd.Value);
                break;

            case SpaPointer p:
                MemoryMarshal.Write<uint>(body, (uint)p.PointerType);
                if (IntPtr.Size == 8)
                    MemoryMarshal.Write<ulong>(body[8..], p.Address);
                else
                    MemoryMarshal.Write<uint>(body[8..], (uint)p.Address);
                break;

            case SpaArray a:
            {
                int childSize = ChildSize(a.ChildType, a.Items);
                MemoryMarshal.Write<uint>(body, (uint)childSize);
                MemoryMarshal.Write<uint>(body[4..], (uint)a.ChildType);
                WriteChildren(a.Items, childSize, body[8..]);
                break;
            }

            case SpaChoice c:
            {
                int childSize = ChildSize(c.ChildType, c.Alternatives);
                MemoryMarshal.Write<uint>(body, (uint)c.Kind);
                MemoryMarshal.Write<uint>(body[4..], 0); // flags
                MemoryMarshal.Write<uint>(body[8..], (uint)childSize);
                MemoryMarshal.Write<uint>(body[12..], (uint)c.ChildType);
                WriteChildren(c.Alternatives, childSize, body[16..]);
                break;
            }

            case SpaStruct s:
            {
                int offset = 0;
                foreach (SpaValue field in s.Fields)
                {
                    WriteValue(field, body[offset..]);
                    offset += Pad(8 + BodySize(field));
                }

                break;
            }

            case SpaObject o:
            {
                MemoryMarshal.Write<uint>(body, (uint)o.ObjectType);
                MemoryMarshal.Write<uint>(body[4..], (uint)o.ObjectId);
                int offset = 8;
                foreach (SpaProperty property in o.Properties)
                {
                    MemoryMarshal.Write<uint>(body[offset..], property.Key.Value);
                    MemoryMarshal.Write<uint>(body[(offset + 4)..], property.Flags);
                    WriteValue(property.Value, body[(offset + 8)..]);
                    offset += 8 + Pad(8 + BodySize(property.Value));
                }

                break;
            }

            case SpaSequence s:
            {
                MemoryMarshal.Write<uint>(body, s.Unit);
                MemoryMarshal.Write<uint>(body[4..], 0); // pad
                int offset = 8;
                foreach (SpaControl control in s.Controls)
                {
                    MemoryMarshal.Write<uint>(body[offset..], control.Offset);
                    MemoryMarshal.Write<uint>(body[(offset + 4)..], control.Type);
                    WriteValue(control.Value, body[(offset + 8)..]);
                    offset += 8 + Pad(8 + BodySize(control.Value));
                }

                break;
            }

            case SpaNestedPod p:
                WriteValue(p.Value, body);
                break;

            case SpaUnknown u:
                u.Body.AsSpan().CopyTo(body);
                break;

            default:
                // Unreachable: the hierarchy is closed and every case above is covered. Kept so a
                // future addition fails loudly here rather than writing a silently empty body.
                throw new NotSupportedException($"No writer for a {value.Type} value.");
        }
    }

    private static void WriteChildren(ImmutableArray<SpaValue> children, int childSize, Span<byte> destination)
    {
        int offset = 0;
        foreach (SpaValue child in children)
        {
            int size = Math.Min(childSize, BodySize(child));
            WriteBody(child, destination.Slice(offset, size));
            offset += childSize;
        }
    }

    private static int Pad(int size) => checked(size + 7) & ~7;
}
