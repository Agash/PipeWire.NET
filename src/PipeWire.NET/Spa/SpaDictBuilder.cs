using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Spa;

// spa_dict wire shape (spa/utils/dict.h):
//
//   struct spa_dict      { uint32 flags; uint32 n_items; const spa_dict_item *items; }
//   struct spa_dict_item { const char *key; const char *value; }
//
// Both members of an item are NUL-terminated C strings that the callee reads, and usually strdup's,
// before returning. Mirrors SpaPodBuilder: a ref struct writing into caller-supplied Spans, every
// mutator returning void so chaining on a copy is a compile error, and no heap allocation.
//
//   Span<byte> scratch = stackalloc byte[512];
//   Span<spa_dict_item> items = stackalloc spa_dict_item[8];
//   var d = new SpaDictBuilder(scratch, items);
//   d.Add("media.class"u8, "Audio/Sink"u8);
//   d.Add("node.name"u8, name);
//   d.Add("link.output.port"u8, portId);
//   spa_dict dict = d.Build();
//
// Keys and values are copied into `scratch`, including UTF-8 literals that could be referenced in
// place. That costs a few bytes of memcpy on a once-per-object call, and in exchange the builder
// never holds a pointer into memory it does not own, so no call site has to reason about pinning.

/// <summary>
/// Builds a native <c>spa_dict</c> into caller-supplied <see cref="Span{T}"/> buffers without heap
/// allocation. Every mutator returns <see langword="void"/>; call them on the same instance
/// (this is a <see langword="ref struct"/> - chaining would mutate a copy).
/// </summary>
/// <remarks>
/// The <c>spa_dict</c> returned by <see cref="Build"/> points into the caller's buffers and is valid
/// only while they are in scope. Pass it straight to the native call; never store it.
/// </remarks>
internal unsafe ref struct SpaDictBuilder
{
    private readonly Span<byte> _scratch;
    private readonly Span<spa_dict_item> _items;
    private int _used;
    private int _count;

    /// <param name="scratch">
    /// Backing store for the NUL-terminated key and value bytes. Must not be GC-movable; a
    /// <c>stackalloc</c> buffer in the calling frame is the intended shape.
    /// </param>
    /// <param name="items">Backing store for the item array; must hold every item added.</param>
    public SpaDictBuilder(Span<byte> scratch, Span<spa_dict_item> items)
    {
        _scratch = scratch;
        _items = items;
        _used = 0;
        _count = 0;
    }

    /// <summary>Adds a key/value pair, both given as UTF-8.</summary>
    public void Add(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value) =>
        Append(CopyUtf8(key), CopyUtf8(value));

    /// <summary>Adds a key whose value is a managed string.</summary>
    public void Add(scoped ReadOnlySpan<byte> key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Append(CopyUtf8(key), EncodeUtf8(value));
    }

    /// <summary>Adds a key whose value is a PipeWire global id, formatted in place.</summary>
    public void Add(scoped ReadOnlySpan<byte> key, uint value) =>
        Append(CopyUtf8(key), FormatUtf8(value));

    /// <summary>The number of items added so far.</summary>
    public readonly int Count => _count;

    /// <summary>
    /// The completed dictionary, pointing into the caller's buffers and valid only while they are
    /// in scope.
    /// </summary>
    public readonly spa_dict Build() => new()
    {
        // Must be zero: SPA_DICT_FLAG_SORTED is bit 0, and a stray set bit tells PipeWire the items
        // are sorted, after which it may binary-search an unsorted array and miss properties.
        flags = 0,
        n_items = (uint)_count,
        items = (spa_dict_item*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(_items)),
    };

    private void Append(sbyte* key, sbyte* value)
    {
        if (_count == _items.Length)
            throw new InvalidOperationException(
                $"SpaDictBuilder item buffer holds {_items.Length} entries; size it to the property count.");
        _items[_count++] = new spa_dict_item { key = key, value = value };
    }

    private sbyte* CopyUtf8(scoped ReadOnlySpan<byte> utf8)
    {
        Span<byte> dst = Reserve(utf8.Length + 1);
        utf8.CopyTo(dst);
        dst[utf8.Length] = 0;
        return Head(dst);
    }

    private sbyte* EncodeUtf8(string value)
    {
        Span<byte> dst = Reserve(Encoding.UTF8.GetByteCount(value) + 1);
        int written = Encoding.UTF8.GetBytes(value, dst);
        dst[written] = 0;
        return Head(dst);
    }

    private sbyte* FormatUtf8(uint value)
    {
        Span<byte> dst = Reserve(11);
        _ = value.TryFormat(dst, out int written);
        dst[written] = 0;
        return Head(dst);
    }

    private Span<byte> Reserve(int bytes)
    {
        if (_scratch.Length - _used < bytes)
            throw new InvalidOperationException(
                $"SpaDictBuilder scratch buffer of {_scratch.Length} bytes is exhausted; enlarge it.");
        Span<byte> slice = _scratch.Slice(_used, bytes);
        _used += bytes;
        return slice;
    }

    private static sbyte* Head(Span<byte> s) =>
        (sbyte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(s));
}
