using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PipeWire.NET.Spa;

// SPA POD wire format (docs.pipewire.org/page_spa_pod.html):
//
//   [uint32 size][uint32 type][payload...][0-padding to 8-byte boundary]
//
// The SPA pod_builder C API is varargs macros that ClangSharp cannot translate.
// This builder reimplements the wire format in pure C# writing to a caller-supplied
// Span<byte>. Design inspired by pipewire-rs libspa::pod::serialize.
//
// SpaPodBuilder is a `ref struct` and every mutator returns `void` ON PURPOSE: a
// fluent-chaining API (`b.Add(...).Add(...)`) on a mutable struct would mutate a
// returned COPY, silently corrupting the output. void returns make that a compile
// error - always call methods on the same instance:
//
//   var b = new SpaPodBuilder(stackalloc byte[512]);
//   b.PushObject(SpaType.ObjectFormat, SpaParamType.EnumFormat);
//   b.AddId(SpaFormat.MediaType, SpaMediaType.Video);
//   b.AddChoiceEnum(SpaFormat.VideoFormat, SpaVideoFormat.Bgra, SpaVideoFormat.Rgba);
//   ReadOnlySpan<byte> pod = b.GetPod();

/// <summary>
/// Writes SPA POD objects into a <see cref="Span{T}"/> without heap allocation.
/// Every mutator returns <see langword="void"/>; call them on the same instance
/// (this is a <see langword="ref struct"/> - chaining would mutate a copy).
/// </summary>
internal ref struct SpaPodBuilder
{
    private readonly Span<byte> _buf;
    private int _pos;

    // Stack of open object/array offsets (for back-patching sizes).
    // Maximum nesting depth of 8 covers all real-world PipeWire format objects.
    private OffsetStack _stack;
    private int _depth;

    [InlineArray(8)]
    private struct OffsetStack { private int _e; }

    public SpaPodBuilder(Span<byte> buffer)
    {
        _buf   = buffer;
        _pos   = 0;
        _depth = 0;
    }

    // - Bare primitive values (rarely used directly; prefer the keyed overloads) -

    public void AddInt(int value)       => WritePod(SpaType.Int,    value);
    public void AddFloat(float value)   => WritePod(SpaType.Float,  value);
    public void AddDouble(double value) => WritePod(SpaType.Double, value);
    public void AddBool(bool value)     => WritePod(SpaType.Bool,   value ? 1 : 0);
    public void AddId(SpaIdValue id)    => WritePod(SpaType.Id,     (int)id.Value);
    public void AddLong(long value)     => WritePodLong(value);

    // - Keyed properties (inside an Object) -

    public void AddId(SpaKey key, SpaIdValue value)            { WritePropHeader(key); AddId(value); }
    public void AddInt(SpaKey key, int value)            { WritePropHeader(key); AddInt(value); }
    public void AddInt(SpaKey key, int value, uint propFlags) { WritePropHeader(key, propFlags); AddInt(value); }
    public void AddLong(SpaKey key, long value)          { WritePropHeader(key); AddLong(value); }
    public void AddLong(SpaKey key, long value, uint propFlags) { WritePropHeader(key, propFlags); AddLong(value); }
    public void AddFraction(SpaKey key, uint n, uint d)  { WritePropHeader(key); WriteFraction(n, d); }
    public void AddRectangle(SpaKey key, uint w, uint h) { WritePropHeader(key); WriteRectangle(w, h); }

    // - Choice: Enum over Long (DRM format modifiers) -

    /// <summary>
    /// Choice(Enum) over <c>Long</c> values - used to offer DRM format modifiers. The first
    /// modifier is the preferred/default; the rest are alternatives. Pass
    /// <see cref="SpaPodPropFlag.Mandatory"/> | <see cref="SpaPodPropFlag.DontFixate"/> as
    /// <paramref name="propFlags"/> on the first negotiation pass so the producer narrows the set
    /// to what it supports without fixating, then re-offer a single modifier to fixate.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="values"/> is empty.</exception>
    public void AddChoiceEnumLong(SpaKey key, ReadOnlySpan<long> values, uint propFlags = 0)
    {
        // A choice with no children is a header claiming alternatives it does not carry. Our own
        // reader rejects it, and a producer reading it has nothing to select, so refuse to write
        // one rather than emit a pod that only fails later on someone else's parser.
        if (values.IsEmpty)
            throw new ArgumentException("a Choice must offer at least one value.", nameof(values));

        WritePropHeader(key, propFlags);
        int start = _pos;
        WriteU32(0u);                // pod size - back-patched
        WriteU32(SpaType.Choice);
        WriteU32(SpaChoiceType.Enum);
        WriteU32(0u);                // choice flags
        WriteU32(8);                // child.size - Long is 8 bytes
        WriteU32(SpaType.Long);     // child.type
        // SPA Choice Enum layout is { default, alt0, alt1, ... }: the FIRST child is the default/preferred
        // value and the rest are the selectable alternatives, so the default must also appear among the
        // alternatives. Emit values[0] once as the default and then every value. A single modifier written
        // once would be a default with NO alternatives - the consumer then has nothing to select and dmabuf
        // negotiation fails ("no more input formats"). Matches pipewire's video-src-fixate.c, which writes the
        // first modifier twice.
        WriteI64(values[0]);

        foreach (long v in values)
            WriteI64(v);

        PatchSize(start);
        Align8();
    }

    // - Choice: Enum (list of allowed Id values) -

    /// <exception cref="ArgumentException"><paramref name="values"/> is empty.</exception>
    public void AddChoiceEnum(SpaKey key, params ReadOnlySpan<SpaIdValue> values)
    {
        if (values.IsEmpty)
            throw new ArgumentException("a Choice must offer at least one value.", nameof(values));

        // SPA Choice wire format (spa/pod/pod.h):
        //   [size][type=Choice]                            <- pod header
        //   [choiceType][flags][child.size][child.type]    <- spa_pod_choice_body
        //   [value0][value1][...]                          <- raw values, child.size each
        WritePropHeader(key);
        int start = _pos;
        WriteU32(0u);                // pod size - back-patched
        WriteU32(SpaType.Choice);
        WriteU32(SpaChoiceType.Enum);
        WriteU32(0u);                // flags
        WriteU32(4);                // child.size - Id is 4 bytes
        WriteU32(SpaType.Id);       // child.type

        // { default, alt0, alt1, ... }: the first child is the preferred value and the rest are the
        // selectable alternatives, so the default has to appear again among them. Written once, the
        // first value becomes a default that is not itself selectable - the same mistake the Long
        // variant documents, and it silently removes the preferred format from the offer.
        WriteU32(values[0]);

        foreach (SpaIdValue v in values)
            WriteU32(v);
        PatchSize(start);
        Align8();
    }

    /// <summary>Choice(Range) over Int - default, min, max.</summary>
    public void AddChoiceRangeInt(SpaKey key, int def, int min, int max)
    {
        WritePropHeader(key);
        int start = _pos;
        WriteU32(0u);
        WriteU32(SpaType.Choice);
        WriteU32(SpaChoiceType.Range);
        WriteU32(0u);                // flags
        WriteU32(4);                // child.size - Int = 4
        WriteU32(SpaType.Int);
        WriteU32((uint)def); WriteU32((uint)min); WriteU32((uint)max);
        PatchSize(start);
        Align8();
    }

    /// <summary>Choice(Flags) over Int - a single bitmask value (e.g. allowed buffer data types).</summary>
    /// <remarks>
    /// One value, not two. spa/pod/pod.h documents SPA_CHOICE_Flags as "first value is flags",
    /// unlike Range and Enum which take a default plus alternatives. Writing a second value as a
    /// mask is not what a reader expects.
    /// </remarks>
    public void AddChoiceFlagsInt(SpaKey key, int flags)
    {
        WritePropHeader(key);
        int start = _pos;
        WriteU32(0u);
        WriteU32(SpaType.Choice);
        WriteU32(SpaChoiceType.Flags);
        WriteU32(0u);                // flags
        WriteU32(4);                // child.size - Int = 4
        WriteU32(SpaType.Int);
        WriteU32((uint)flags);
        PatchSize(start);
        Align8();
    }

    // - Choice: Range (default, min, max) -

    public void AddChoiceRangeRectangle(SpaKey key,
        uint defaultW, uint defaultH,
        uint minW,     uint minH,
        uint maxW,     uint maxH)
    {
        WritePropHeader(key);
        int start = _pos;
        WriteU32(0u);
        WriteU32(SpaType.Choice);
        WriteU32(SpaChoiceType.Range);
        WriteU32(0u);                // flags
        WriteU32(8);                // child.size - Rectangle = 8 bytes
        WriteU32(SpaType.Rectangle);
        WriteU32(defaultW); WriteU32(defaultH);
        WriteU32(minW);     WriteU32(minH);
        WriteU32(maxW);     WriteU32(maxH);
        PatchSize(start);
        Align8();
    }

    public void AddChoiceRangeFraction(SpaKey key,
        uint defaultNum, uint defaultDenom,
        uint minNum,     uint minDenom,
        uint maxNum,     uint maxDenom)
    {
        WritePropHeader(key);
        int start = _pos;
        WriteU32(0u);
        WriteU32(SpaType.Choice);
        WriteU32(SpaChoiceType.Range);
        WriteU32(0u);                // flags
        WriteU32(8);                // child.size - Fraction = 8 bytes
        WriteU32(SpaType.Fraction);
        WriteU32(defaultNum); WriteU32(defaultDenom);
        WriteU32(minNum);     WriteU32(minDenom);
        WriteU32(maxNum);     WriteU32(maxDenom);
        PatchSize(start);
        Align8();
    }

    // - Object -

    public void PushObject(SpaType objectType, SpaParamType paramId)
    {
        Push(_pos);
        WriteU32(0u);                // size placeholder - back-patched in Pop
        WriteU32(SpaType.Object);
        WriteU32((uint)objectType);
        WriteU32((uint)paramId);
    }

    // - Sequence -

    /// <summary>Opens a timed sequence. Close it with <see cref="Pop"/>.</summary>
    /// <param name="unit">
    /// What the control offsets are counted in. Zero means samples, which is what a stream sends.
    /// </param>
    /// <remarks>
    /// The allocation-free counterpart to writing a <see cref="SpaSequence"/> through
    /// <c>SpaPod.ToBytes</c>. It exists because the thing that fills a sequence is the process
    /// callback: per-buffer MIDI and timed automation are written on the realtime thread, where
    /// building a value tree and then serialising it is the wrong shape.
    /// <para>
    /// The body carries a <c>unit</c> and a <c>pad</c> word before the controls
    /// (<c>spa/pod/pod.h:224-228</c>). The pad is written and is not optional: the first control
    /// starts eight bytes into the body, and a reader that finds one at four reads its offset out
    /// of the unit.
    /// </para>
    /// </remarks>
    public void PushSequence(uint unit = 0)
    {
        Push(_pos);
        WriteU32(0u);                // size placeholder - back-patched in Pop
        WriteU32(SpaType.Sequence);
        WriteU32(unit);
        WriteU32(0u);                // pad
    }

    /// <summary>Writes a control header. The caller writes its value pod next.</summary>
    /// <param name="offset">When it applies, in the sequence's unit.</param>
    /// <param name="type">What the value means.</param>
    /// <remarks>
    /// Header then value, the same shape as a keyed property inside an object, so the existing value
    /// writers serve both. Controls must be written in ascending offset order; nothing here enforces
    /// it, because the check costs a comparison on the realtime path and the daemon reads them in
    /// the order they appear either way.
    /// </remarks>
    public void AddControl(uint offset, SpaControlType type)
    {
        // spa_pod_control header: offset (uint32) + type (uint32), then the value pod.
        WriteU32(offset);
        WriteU32((uint)type);
    }

    /// <summary>Writes a whole control whose value is raw bytes: MIDI, UMP or OSC.</summary>
    /// <param name="offset">When it applies, in the sequence's unit.</param>
    /// <param name="type">What the bytes are.</param>
    /// <param name="data">The payload.</param>
    /// <remarks>
    /// The three byte-valued control types are what a sequence usually carries, and writing one by
    /// hand is a header, a pod header and a copy with the padding easy to get wrong.
    /// </remarks>
    public void AddControl(uint offset, SpaControlType type, scoped ReadOnlySpan<byte> data)
    {
        AddControl(offset, type);
        AddBytes(data);
    }

    /// <summary>Writes a bare bytes pod.</summary>
    public void AddBytes(scoped ReadOnlySpan<byte> data)
    {
        WriteU32((uint)data.Length);
        WriteU32(SpaType.Bytes);
        EnsureRoom(data.Length);
        data.CopyTo(_buf.Slice(_pos, data.Length));
        _pos += data.Length;
        Align8();
    }

    /// <summary>Closes the innermost open object, back-patching its size.</summary>
    /// <exception cref="InvalidOperationException">Nothing is open.</exception>
    public void Pop()
    {
        // Without this the index goes negative and back-patches a size at whatever sits before the
        // stack, which is silent corruption rather than a caller error.
        if (_depth == 0)
            throw new InvalidOperationException("SpaPodBuilder: Pop with nothing open.");

        int start = _stack[--_depth];
        PatchSize(start);
        Align8();
    }

    /// <summary>Closes anything still open and returns the complete POD bytes.</summary>
    public ReadOnlySpan<byte> GetPod()
    {
        while (_depth > 0) Pop();
        return _buf[.._pos];
    }

    // - Private helpers -

    private void WritePod<T>(SpaType type, T value) where T : struct
    {
        int size = Unsafe.SizeOf<T>();
        WriteU32((uint)size);
        WriteU32(type);
        EnsureRoom(size);
        MemoryMarshal.Write(_buf.Slice(_pos, size), value);
        _pos += size;
        Align8();
    }

    private void WritePodLong(long value)
    {
        WriteU32(8);
        WriteU32(SpaType.Long);
        WriteI64(value);            // already 8-byte aligned
    }

    private void WriteFraction(uint num, uint denom)
    {
        WriteU32(8);                // sizeof(spa_fraction) = 2xuint32
        WriteU32(SpaType.Fraction);
        WriteU32(num);
        WriteU32(denom);            // already aligned
    }

    private void WriteRectangle(uint width, uint height)
    {
        WriteU32(8);                // sizeof(spa_rectangle) = 2xuint32
        WriteU32(SpaType.Rectangle);
        WriteU32(width);
        WriteU32(height);           // already aligned
    }

    private void WritePropHeader(SpaKey key, uint flags = 0)
    {
        // spa_pod_prop header: key (uint32) + flags (uint32), then the value pod.
        WriteU32(key);
        WriteU32(flags);
    }

    /// <summary>Back-patches the 4-byte size field of a pod whose header starts at <paramref name="start"/>.</summary>
    /// <remarks>
    /// Called before the trailing <see cref="Align8"/>, never after. A pod's size counts its body
    /// only; the padding that follows belongs between pods. Including it makes a reader compute one
    /// child too many for any odd number of four-byte children - a range choice writes three, so it
    /// came back with a fourth alternative made of the padding.
    /// </remarks>
    private void PatchSize(int start)
    {
        uint bodySize = (uint)(_pos - start - 8);
        MemoryMarshal.Write(_buf.Slice(start, 4), bodySize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // Typed overloads so the body of a writer names the SPA type it is emitting rather than
    // casting it to a number at every call.
    private void WriteU32(SpaType value)       => WriteU32((uint)value);
    private void WriteU32(SpaChoiceType value) => WriteU32((uint)value);
    private void WriteU32(SpaParamType value)  => WriteU32((uint)value);

    private void WriteU32(uint value)
    {
        EnsureRoom(4);
        MemoryMarshal.Write(_buf.Slice(_pos, 4), value);
        _pos += 4;
    }

    private void WriteI64(long value)
    {
        EnsureRoom(8);
        MemoryMarshal.Write(_buf.Slice(_pos, 8), value);
        _pos += 8;
    }

    /// <remarks>
    /// The pad is cleared, not skipped. A pod's padding goes on the wire like the rest of it, so
    /// leaving it as whatever the caller's buffer already held makes the bytes non-deterministic
    /// and hands the daemon whatever that buffer was last used for. Callers that pass a fresh
    /// stackalloc get zeroes either way; one reusing a scratch buffer does not.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Align8()
    {
        int rem = _pos & 7;
        if (rem == 0) return;

        int pad = 8 - rem;
        EnsureRoom(pad);
        _buf.Slice(_pos, pad).Clear();
        _pos += pad;
    }

    /// <summary>Fails with the builder's own error rather than out of a <c>Slice</c> deep inside.</summary>
    /// <remarks>
    /// Every write goes through here. Without it an over-long pod surfaces as an
    /// <see cref="ArgumentOutOfRangeException"/> naming a span offset, which tells a caller nothing
    /// about which buffer to enlarge.
    /// </remarks>
    private readonly void EnsureRoom(int bytes)
    {
        if (bytes > _buf.Length - _pos)
            ThrowBufferExhausted(bytes, _buf.Length - _pos, _buf.Length);
    }

    private static void ThrowBufferExhausted(int needed, int left, int capacity) =>
        throw new InvalidOperationException(
            $"SpaPodBuilder needs {needed} more bytes and {left} of {capacity} remain; "
            + "size the buffer to the pod.");

    private void Push(int offset)
    {
        if (_depth >= 8) ThrowNestingOverflow();
        _stack[_depth++] = offset;
    }

    private static void ThrowNestingOverflow() =>
        throw new InvalidOperationException("SpaPodBuilder: nesting depth exceeds 8.");
}
