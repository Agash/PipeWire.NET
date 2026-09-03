using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PipeWire.NET.Spa;

// SPA POD wire format (docs.pipewire.org/page_spa_pod.html):
//
//   [uint32 size][uint32 type][payload size bytes][padding to 8-byte boundary]
//
// SpaPodReader walks a top-level pod, decoding object properties and primitive values.
// Mirror of SpaPodBuilder; pure C#, no native dispatch, NativeAOT-safe.

/// <summary>
/// Parses SPA POD wire-format bytes without allocation.
/// </summary>
/// <remarks>
/// Use one of the static <c>Read*</c> helpers to extract a single value, or call
/// <see cref="EnterObject"/> + <see cref="TryReadProperty(out SpaKey, out SpaPodReader)"/> in a loop
/// to walk an object.
/// </remarks>
internal ref struct SpaPodReader
{
    private readonly ReadOnlySpan<byte> _buf;
    private int _pos;

    public SpaPodReader(ReadOnlySpan<byte> buffer)
    {
        _buf = buffer;
        _pos = 0;
    }

    /// <summary>Total size of the buffer in bytes.</summary>
    public int Length => _buf.Length;

    /// <summary>Current read offset.</summary>
    public int Position => _pos;

    /// <summary>True when no more bytes remain to read.</summary>
    public bool IsAtEnd => _pos >= _buf.Length;

    // - Object iteration -

    /// <summary>
    /// Reads the outermost pod header. Succeeds only when the pod is a SPA object;
    /// returns the object's type, id, and remaining body size.
    /// </summary>
    public bool EnterObject(out uint objectType, out uint objectId, out uint bodySize)
    {
        objectType = 0; objectId = 0; bodySize = 0;
        if (!TryReadHeader(out uint size, out SpaType type)) return false;
        if (type != SpaType.Object) return false;

        // An object body always carries at least its type and id. Without this check `size - 8`
        // underflows for a malformed pod and reports a body of nearly 4GB.
        if (size < 8) return false;

        if (!TryReadU32(out objectType)) return false;
        if (!TryReadU32(out objectId))   return false;
        bodySize = size - 8; // size includes the object-type + object-id 8 bytes already consumed

        // Property iteration stops at the object's own end, not the buffer's. An object nested in a
        // struct is followed by its siblings, and without this the walk reads them as further
        // properties of this object.
        _objectEnd = _pos + (int)bodySize;
        return true;
    }

    // Where the object entered by EnterObject ends. Null until one is entered, in which case the
    // whole buffer is the bound - a reader handed a single value pod has no object to be inside of.
    private int? _objectEnd;

    /// <summary>
    /// Reads the next property in the current object.
    /// </summary>
    /// <param name="key">The property key, comparable against any of the SPA key enums.</param>
    /// <param name="value">Reader positioned at the property's value pod.</param>
    /// <returns><see langword="false"/> when no more properties remain in the current object body.</returns>
    public bool TryReadProperty(out SpaKey key, out SpaPodReader value) =>
        TryReadProperty(out key, out _, out value);

    /// <summary>
    /// Reads the next property and also reports its <c>spa_pod_prop</c> flags (e.g.
    /// <see cref="SpaPodPropFlag.DontFixate"/> on an unfixated modifier choice).
    /// </summary>
    public bool TryReadProperty(out SpaKey key, out uint flags, out SpaPodReader value)
    {
        key = default;
        flags = 0;
        value = default;

        // spa_pod_prop header = [uint32 key][uint32 flags][value pod...]
        int end = _objectEnd ?? _buf.Length;
        if (_pos + 8 > end) return false;
        if (!TryReadU32(out uint rawKey)) return false;
        key = SpaKey.FromRaw(rawKey);
        if (!TryReadU32(out flags))  return false; // flags

        // The value pod sits at the current offset. Peek its size.
        if (_pos + 8 > end) return false;
        // Checked against what is left before any arithmetic. Casting first and checking after is
        // not enough: a size of uint.MaxValue casts to -1 and passes the bounds test, and one near
        // int.MaxValue overflows the addition to a negative length that then throws out of Slice -
        // from a parser whose whole contract is that malformed input returns false.
        uint vSize = MemoryMarshal.Read<uint>(_buf.Slice(_pos, 4));
        if (vSize > (uint)(end - _pos - 8)) return false;

        int valueLen = 8 + (int)vSize;          // pod header + body
        int valueLenAligned = (valueLen + 7) & ~7;

        value = new SpaPodReader(_buf.Slice(_pos, valueLen));
        _pos += valueLenAligned;
        return true;
    }

    // - Primitive readers (assume reader is positioned at a single value pod) -

    public int ReadInt()
    {
        ReadHeaderOrThrow(SpaType.Int, expectedSize: 4);
        int v = MemoryMarshal.Read<int>(_buf.Slice(_pos, 4));
        _pos += 4; AlignTo8();
        return v;
    }

    public long ReadLong()
    {
        ReadHeaderOrThrow(SpaType.Long, expectedSize: 8);
        long v = MemoryMarshal.Read<long>(_buf.Slice(_pos, 8));
        _pos += 8;
        return v;
    }

    public float ReadFloat()
    {
        ReadHeaderOrThrow(SpaType.Float, expectedSize: 4);
        float v = MemoryMarshal.Read<float>(_buf.Slice(_pos, 4));
        _pos += 4; AlignTo8();
        return v;
    }

    public double ReadDouble()
    {
        ReadHeaderOrThrow(SpaType.Double, expectedSize: 8);
        double v = MemoryMarshal.Read<double>(_buf.Slice(_pos, 8));
        _pos += 8;
        return v;
    }

    public bool ReadBool()
    {
        ReadHeaderOrThrow(SpaType.Bool, expectedSize: 4);
        int v = MemoryMarshal.Read<int>(_buf.Slice(_pos, 4));
        _pos += 4; AlignTo8();
        return v != 0;
    }

    public SpaIdValue ReadId()
    {
        ReadHeaderOrThrow(SpaType.Id, expectedSize: 4);
        uint v = MemoryMarshal.Read<uint>(_buf.Slice(_pos, 4));
        _pos += 4; AlignTo8();
        return SpaIdValue.FromRaw(v);
    }

    public (uint Width, uint Height) ReadRectangle()
    {
        ReadHeaderOrThrow(SpaType.Rectangle, expectedSize: 8);
        uint w = MemoryMarshal.Read<uint>(_buf.Slice(_pos, 4)); _pos += 4;
        uint h = MemoryMarshal.Read<uint>(_buf.Slice(_pos, 4)); _pos += 4;
        return (w, h);
    }

    public (uint Numerator, uint Denominator) ReadFraction()
    {
        ReadHeaderOrThrow(SpaType.Fraction, expectedSize: 8);
        uint n = MemoryMarshal.Read<uint>(_buf.Slice(_pos, 4)); _pos += 4;
        uint d = MemoryMarshal.Read<uint>(_buf.Slice(_pos, 4)); _pos += 4;
        return (n, d);
    }

    /// <summary>
    /// Reads a DRM format modifier value pod: either a plain <c>Long</c> or a Choice(Enum) of
    /// <c>Long</c>s. Returns the first (preferred) modifier in <paramref name="first"/> and how many
    /// the pod carried in <paramref name="count"/> - so a caller distinguishes a fixated single
    /// modifier (count == 1) from a still-open choice (count &gt; 1) it must fixate. Allocation-free:
    /// the values are scanned in place, none are materialised.
    /// </summary>
    /// <returns><see langword="false"/> when the pod is neither a Long nor a Long choice.</returns>
    public bool TryReadModifier(out long first, out int count)
    {
        first = 0;
        count = 0;
        int savedPos = _pos;
        if (!TryReadHeader(out uint size, out SpaType type)) { _pos = savedPos; return false; }

        if (type == SpaType.Long)
        {
            if (size < 8 || _pos + 8 > _buf.Length) { _pos = savedPos; return false; }
            first = MemoryMarshal.Read<long>(_buf.Slice(_pos, 8));
            _pos += 8;
            count = 1;
            return true;
        }

        if (type != SpaType.Choice) { _pos = savedPos; return false; }

        // spa_pod_choice_body: [choiceType][flags][childSize][childType], then the values.
        if (_pos + 16 > _buf.Length) { _pos = savedPos; return false; }
        if (!TryReadU32(out uint choiceType))  { _pos = savedPos; return false; }
        if (!TryReadU32(out _))               { _pos = savedPos; return false; } // flags
        if (!TryReadU32(out uint childSize))  { _pos = savedPos; return false; }
        if (!TryReadU32(out uint childType))  { _pos = savedPos; return false; }
        if ((SpaType)childType != SpaType.Long || childSize != 8) { _pos = savedPos; return false; }

        // Which kind of choice it is decides what the values mean. Enum is { default, alt... } and
        // None is a single value; Range is { default, min, max } and Step adds a stride, and reading
        // either of those as a modifier set reports a minimum and a maximum as two modifiers on
        // offer. Only the two kinds whose children are all modifiers are accepted.
        if ((SpaChoiceType)choiceType is not (SpaChoiceType.Enum or SpaChoiceType.None))
        {
            _pos = savedPos;
            return false;
        }

        // The choice body length (size) covers the 16-byte header + N child values. Compared
        // unsigned before the cast, like every other bound in this file: size is the producer's
        // word, and casting first is what turns a large one into a negative length.
        if (size < 16 + 8u) { _pos = savedPos; return false; }
        uint valuesLen = size - 16;
        if (valuesLen % 8 != 0 || valuesLen > (uint)(_buf.Length - _pos))
        {
            _pos = savedPos;
            return false;
        }

        // We only need the first value and the count, so read the first in place and skip the rest -
        // nothing allocated.
        int valuesBytes = (int)valuesLen;
        first = MemoryMarshal.Read<long>(_buf.Slice(_pos, 8));
        count = valuesBytes / 8;
        _pos += valuesBytes;
        return true;
    }

    /// <summary>
    /// If the current value pod is a Choice, advances past the choice header and returns
    /// a reader positioned at the first concrete value. Useful when format negotiation
    /// returns Choice-wrapped values.
    /// </summary>
    public bool TryUnwrapChoice(out SpaPodReader first)
    {
        first = default;
        // Peek the header non-destructively: a plain (non-choice) value must be left
        // untouched so the caller can fall back to ReadId()/ReadRectangle()/etc.
        int savedPos = _pos;
        if (!TryReadHeader(out uint size, out SpaType type) || type != SpaType.Choice)
        {
            _pos = savedPos;
            return false;
        }

        // Every exit below restores the position. The caller falls back to a plain typed read on
        // this same reader when a choice is declined, so leaving the position moved does not fail,
        // it silently reads the wrong bytes as the value.
        if (_pos + 16 > _buf.Length
            || !TryReadU32(out _)                    // choiceType
            || !TryReadU32(out _)                    // flags
            || !TryReadU32(out uint childSize)
            || !TryReadU32(out uint childType))
        {
            _pos = savedPos;
            return false;
        }

        // Rebuild a synthetic pod header so the returned reader can call ReadXxx directly.
        // Compared against the bytes that are left, unsigned, the way every other check in this
        // file does it. Casting first and then adding overflows int for a childSize near its
        // maximum, and the sum wraps negative so the comparison passes: Slice then throws where
        // this contract says it returns false, and the caller's catch does not expect that type.
        if (childSize > (uint)(_buf.Length - _pos))
        {
            _pos = savedPos;
            return false;
        }

        int bodyLen = (int)childSize;

        ReadOnlySpan<byte> body = _buf.Slice(_pos, bodyLen);
        // The caller cannot mutate a ReadOnlySpan, so the body is exposed through a child reader
        // that carries its type out of band rather than re-emitting a pod header into it.
        first = new SpaPodReader(body) { _synthesizedType = (SpaType)childType };
        return true;
    }

    // Nullable rather than a zero sentinel: zero is SpaType.Start, a real member, so "unset"
    // has no spare value to borrow.
    private SpaType? _synthesizedType;

    // - Header parsing -

    private bool TryReadHeader(out uint size, out SpaType type)
    {
        size = 0; type = 0;
        if (_pos + 8 > _buf.Length) return false;

        uint declared = MemoryMarshal.Read<uint>(_buf.Slice(_pos, 4));
        uint declaredType = MemoryMarshal.Read<uint>(_buf.Slice(_pos + 4, 4));

        // The size field is attacker- or bug-controlled, so a pod claiming more body than the
        // buffer holds is rejected here rather than handed on as a length someone slices with.
        if (declared > (uint)(_buf.Length - _pos - 8)) return false;

        size = declared;
        type = (SpaType)declaredType;
        _pos += 8;
        return true;
    }

    private void ReadHeaderOrThrow(SpaType expectedType, uint expectedSize)
    {
        // When TryUnwrapChoice synthesized this reader, there is no embedded header
        // - the type is carried out-of-band via _synthesizedType.
        if (_synthesizedType is { } synthesized)
        {
            if (synthesized != expectedType)
                throw new InvalidOperationException(
                    $"SPA pod type mismatch: expected {expectedType}, got synthesized {synthesized}");

            // The body came from a choice's childSize, which the producer chose. A short one -
            // an Id choice declaring one byte per child - would otherwise reach the Slice in the
            // reader below and throw ArgumentOutOfRangeException out of a callback whose catch
            // only names InvalidOperationException, which ends the process rather than the frame.
            if ((uint)(_buf.Length - _pos) < expectedSize)
                throw new InvalidOperationException(
                    $"SPA pod size mismatch: expected {expectedSize}, "
                    + $"synthesized body holds {_buf.Length - _pos}");
            return;
        }

        if (!TryReadHeader(out uint size, out SpaType type))
            throw new InvalidOperationException("Truncated SPA pod.");
        if (type != expectedType)
            throw new InvalidOperationException(
                $"SPA pod type mismatch: expected {expectedType}, got {type}");
        if (size != expectedSize)
            throw new InvalidOperationException(
                $"SPA pod size mismatch: expected {expectedSize}, got {size}");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadU32(out uint value)
    {
        if (_pos + 4 > _buf.Length) { value = 0; return false; }
        value = MemoryMarshal.Read<uint>(_buf.Slice(_pos, 4));
        _pos += 4;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AlignTo8()
    {
        int rem = _pos & 7;
        if (rem != 0) _pos += 8 - rem;
    }
}
