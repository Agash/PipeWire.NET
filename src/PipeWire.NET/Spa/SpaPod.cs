using System.Buffers.Binary;
using System.Collections.Immutable;
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
        return Pad(8 + BodySize(value));
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

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(pod);
        var type = (SpaType)BinaryPrimitives.ReadUInt32LittleEndian(pod[4..]);

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
                value = new SpaBool(BinaryPrimitives.ReadInt32LittleEndian(body) != 0);
                return true;

            case SpaType.Id:
                if (body.Length < 4) return false;
                value = new SpaId(BinaryPrimitives.ReadUInt32LittleEndian(body));
                return true;

            case SpaType.Int:
                if (body.Length < 4) return false;
                value = new SpaInt(BinaryPrimitives.ReadInt32LittleEndian(body));
                return true;

            case SpaType.Long:
                if (body.Length < 8) return false;
                value = new SpaLong(BinaryPrimitives.ReadInt64LittleEndian(body));
                return true;

            case SpaType.Float:
                if (body.Length < 4) return false;
                value = new SpaFloat(BinaryPrimitives.ReadSingleLittleEndian(body));
                return true;

            case SpaType.Double:
                if (body.Length < 8) return false;
                value = new SpaDouble(BinaryPrimitives.ReadDoubleLittleEndian(body));
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
                    BinaryPrimitives.ReadUInt32LittleEndian(body),
                    BinaryPrimitives.ReadUInt32LittleEndian(body[4..]));
                return true;

            case SpaType.Fraction:
                if (body.Length < 8) return false;
                value = new SpaFraction(
                    BinaryPrimitives.ReadUInt32LittleEndian(body),
                    BinaryPrimitives.ReadUInt32LittleEndian(body[4..]));
                return true;

            case SpaType.Bitmap:
                value = new SpaBitmap([.. body]);
                return true;

            case SpaType.Fd:
                if (body.Length < 8) return false;
                value = new SpaFd(BinaryPrimitives.ReadInt64LittleEndian(body));
                return true;

            case SpaType.Pointer:
            {
                // [uint32 type][uint32 padding][pointer], the pointer being native-word sized.
                if (body.Length < 8 + IntPtr.Size) return false;
                var pointerType = (SpaType)BinaryPrimitives.ReadUInt32LittleEndian(body);
                ulong address = IntPtr.Size == 8
                    ? BinaryPrimitives.ReadUInt64LittleEndian(body[8..])
                    : BinaryPrimitives.ReadUInt32LittleEndian(body[8..]);
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

        uint childSize = BinaryPrimitives.ReadUInt32LittleEndian(body);
        var childType = (SpaType)BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
        ReadOnlySpan<byte> items = body[8..];

        // A zero child size with a non-empty body would loop forever; an empty array is legal.
        if (childSize == 0)
        {
            value = items.IsEmpty
                ? new SpaArray(childType, [])
                : null;
            return items.IsEmpty;
        }

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

        value = new SpaStruct(fields.ToImmutable());
        return true;
    }

    private static bool TryParseObject(ReadOnlySpan<byte> body, int depth, out SpaValue? value)
    {
        value = null;
        if (body.Length < 8) return false;

        var objectType = (SpaType)BinaryPrimitives.ReadUInt32LittleEndian(body);
        var objectId = (SpaParamType)BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);

        var properties = ImmutableArray.CreateBuilder<SpaProperty>();
        int offset = 8;
        while (offset + 16 <= body.Length)
        {
            uint key = BinaryPrimitives.ReadUInt32LittleEndian(body[offset..]);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(body[(offset + 4)..]);

            if (!TryParseValue(body[(offset + 8)..], depth + 1, out SpaValue? propertyValue, out int consumed)
                || propertyValue is null || consumed <= 0)
            {
                return false;
            }

            properties.Add(new SpaProperty(key, flags, propertyValue));
            offset += 8 + consumed;
        }

        value = new SpaObject(objectType, objectId, properties.ToImmutable());
        return true;
    }

    private static bool TryParseChoice(ReadOnlySpan<byte> body, int depth, out SpaValue? value)
    {
        value = null;
        if (body.Length < 16) return false;

        var kind = (SpaChoiceType)BinaryPrimitives.ReadUInt32LittleEndian(body);
        uint childSize = BinaryPrimitives.ReadUInt32LittleEndian(body[8..]);
        var childType = (SpaType)BinaryPrimitives.ReadUInt32LittleEndian(body[12..]);
        ReadOnlySpan<byte> items = body[16..];

        if (childSize == 0)
        {
            if (!items.IsEmpty) return false;
            value = new SpaChoice(kind, childType, []);
            return true;
        }

        int count = items.Length / (int)childSize;
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

        uint unit = BinaryPrimitives.ReadUInt32LittleEndian(body);
        var controls = ImmutableArray.CreateBuilder<SpaControl>();

        int offset = 8;
        while (offset + 16 <= body.Length)
        {
            uint controlOffset = BinaryPrimitives.ReadUInt32LittleEndian(body[offset..]);
            uint controlType = BinaryPrimitives.ReadUInt32LittleEndian(body[(offset + 4)..]);

            if (!TryParseValue(body[(offset + 8)..], depth + 1, out SpaValue? controlValue, out int consumed)
                || controlValue is null || consumed <= 0)
            {
                return false;
            }

            controls.Add(new SpaControl(controlOffset, controlType, controlValue));
            offset += 8 + consumed;
        }

        value = new SpaSequence(unit, controls.ToImmutable());
        return true;
    }

    // - Writing -

    private static int BodySize(SpaValue value) => value switch
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
    };

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
            total += Pad(8 + BodySize(value));
        return total;
    }

    private static int SumProperties(ImmutableArray<SpaProperty> properties)
    {
        int total = 0;
        foreach (SpaProperty property in properties)
            total += 8 + Pad(8 + BodySize(property.Value));
        return total;
    }

    private static int SumControls(ImmutableArray<SpaControl> controls)
    {
        int total = 0;
        foreach (SpaControl control in controls)
            total += 8 + Pad(8 + BodySize(control.Value));
        return total;
    }

    private static void WriteValue(SpaValue value, Span<byte> destination)
    {
        int size = BodySize(value);
        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], (uint)value.Type);
        WriteBody(value, destination.Slice(8, size));
    }

    private static void WriteBody(SpaValue value, Span<byte> body)
    {
        switch (value)
        {
            case SpaNone:
                break;

            case SpaBool b:
                BinaryPrimitives.WriteInt32LittleEndian(body, b.Value ? 1 : 0);
                break;

            case SpaId id:
                BinaryPrimitives.WriteUInt32LittleEndian(body, id.Value);
                break;

            case SpaInt i:
                BinaryPrimitives.WriteInt32LittleEndian(body, i.Value);
                break;

            case SpaLong l:
                BinaryPrimitives.WriteInt64LittleEndian(body, l.Value);
                break;

            case SpaFloat f:
                BinaryPrimitives.WriteSingleLittleEndian(body, f.Value);
                break;

            case SpaDouble d:
                BinaryPrimitives.WriteDoubleLittleEndian(body, d.Value);
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
                BinaryPrimitives.WriteUInt32LittleEndian(body, r.Width);
                BinaryPrimitives.WriteUInt32LittleEndian(body[4..], r.Height);
                break;

            case SpaFraction f:
                BinaryPrimitives.WriteUInt32LittleEndian(body, f.Numerator);
                BinaryPrimitives.WriteUInt32LittleEndian(body[4..], f.Denominator);
                break;

            case SpaFd fd:
                BinaryPrimitives.WriteInt64LittleEndian(body, fd.Value);
                break;

            case SpaPointer p:
                BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)p.PointerType);
                if (IntPtr.Size == 8)
                    BinaryPrimitives.WriteUInt64LittleEndian(body[8..], p.Address);
                else
                    BinaryPrimitives.WriteUInt32LittleEndian(body[8..], (uint)p.Address);
                break;

            case SpaArray a:
            {
                int childSize = ChildSize(a.ChildType, a.Items);
                BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)childSize);
                BinaryPrimitives.WriteUInt32LittleEndian(body[4..], (uint)a.ChildType);
                WriteChildren(a.Items, childSize, body[8..]);
                break;
            }

            case SpaChoice c:
            {
                int childSize = ChildSize(c.ChildType, c.Alternatives);
                BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)c.Kind);
                BinaryPrimitives.WriteUInt32LittleEndian(body[4..], 0); // flags
                BinaryPrimitives.WriteUInt32LittleEndian(body[8..], (uint)childSize);
                BinaryPrimitives.WriteUInt32LittleEndian(body[12..], (uint)c.ChildType);
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
                BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)o.ObjectType);
                BinaryPrimitives.WriteUInt32LittleEndian(body[4..], (uint)o.ObjectId);
                int offset = 8;
                foreach (SpaProperty property in o.Properties)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(body[offset..], property.Key.Value);
                    BinaryPrimitives.WriteUInt32LittleEndian(body[(offset + 4)..], property.Flags);
                    WriteValue(property.Value, body[(offset + 8)..]);
                    offset += 8 + Pad(8 + BodySize(property.Value));
                }

                break;
            }

            case SpaSequence s:
            {
                BinaryPrimitives.WriteUInt32LittleEndian(body, s.Unit);
                BinaryPrimitives.WriteUInt32LittleEndian(body[4..], 0); // pad
                int offset = 8;
                foreach (SpaControl control in s.Controls)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(body[offset..], control.Offset);
                    BinaryPrimitives.WriteUInt32LittleEndian(body[(offset + 4)..], control.Type);
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
                throw new NotSupportedException($"No writer for {value.GetType().Name}.");
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

    private static int Pad(int size) => (size + 7) & ~7;
}
