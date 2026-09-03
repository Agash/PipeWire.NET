using System.Collections.Immutable;

namespace PipeWire.NET.Spa;

// One file, because this is a closed union rather than a set of independent types: each case is a
// few lines, means nothing away from the base, and reading them together is how the shape is
// understood. Splitting it would give twenty files nobody opens individually.

/// <summary>
/// A decoded SPA POD value.
/// </summary>
/// <remarks>
/// <para>
/// Every parameter PipeWire exchanges - a format, a volume, a device route - is a POD, and this is
/// what one looks like once it has been read off the wire. The hierarchy is closed: each derived
/// type covers exactly one POD type, so a caller can switch over it and know it has covered
/// everything.
/// </para>
/// <para>
/// A value is immutable and owns no native memory, so it outlives the buffer it came from and can
/// be handed to another thread. Parsing is total - see <see cref="SpaPod.TryParse"/> - so a
/// malformed pod produces a failure rather than an exception or a half-built tree.
/// </para>
/// </remarks>
public abstract record SpaValue
{
    private protected SpaValue() { }

    /// <summary>Which POD type this is.</summary>
    public abstract SpaType Type { get; }
}

/// <summary>An absent value.</summary>
public sealed record SpaNone : SpaValue
{
    /// <summary>The single instance; a none carries no state.</summary>
    public static SpaNone Instance { get; } = new();

    /// <inheritdoc/>
    public override SpaType Type => SpaType.None;
}

/// <summary>A boolean.</summary>
/// <param name="Value">The value.</param>
public sealed record SpaBool(bool Value) : SpaValue
{
    /// <inheritdoc/>
    public override SpaType Type => SpaType.Bool;
}

/// <summary>An enumeration value: a pixel format, a property key, an audio channel.</summary>
/// <param name="Value">The id. What it means depends on the property carrying it.</param>
public sealed record SpaId(uint Value) : SpaValue
{
    /// <inheritdoc/>
    public override SpaType Type => SpaType.Id;
}

/// <summary>A 32-bit signed integer.</summary>
/// <param name="Value">The value.</param>
public sealed record SpaInt(int Value) : SpaValue
{
    /// <inheritdoc/>
    public override SpaType Type => SpaType.Int;
}

/// <summary>A 64-bit signed integer. DRM format modifiers are these.</summary>
/// <param name="Value">The value.</param>
public sealed record SpaLong(long Value) : SpaValue
{
    /// <inheritdoc/>
    public override SpaType Type => SpaType.Long;
}

/// <summary>A 32-bit float. Volumes are these.</summary>
/// <param name="Value">The value.</param>
/// <remarks>
/// Compared by its bits, not by IEEE rules. The wire carries the bits, so a value read back from
/// the daemon has to compare equal to the one written exactly when the bytes match: IEEE equality
/// says two NaNs differ and that positive and negative zero are the same, and both answers are
/// wrong for the one thing this comparison is for.
/// </remarks>
public sealed record SpaFloat(float Value) : SpaValue
{
    /// <inheritdoc/>
    public override SpaType Type => SpaType.Float;

    /// <inheritdoc/>
    public bool Equals(SpaFloat? other) =>
        other is not null
        && BitConverter.SingleToInt32Bits(Value) == BitConverter.SingleToInt32Bits(other.Value);

    /// <inheritdoc/>
    public override int GetHashCode() => BitConverter.SingleToInt32Bits(Value);
}

/// <summary>A 64-bit float.</summary>
/// <param name="Value">The value.</param>
/// <remarks>Compared by its bits, for the reason given on <see cref="SpaFloat"/>.</remarks>
public sealed record SpaDouble(double Value) : SpaValue
{
    /// <inheritdoc/>
    public override SpaType Type => SpaType.Double;

    /// <inheritdoc/>
    public bool Equals(SpaDouble? other) =>
        other is not null
        && BitConverter.DoubleToInt64Bits(Value) == BitConverter.DoubleToInt64Bits(other.Value);

    /// <inheritdoc/>
    public override int GetHashCode() => BitConverter.DoubleToInt64Bits(Value).GetHashCode();
}

/// <summary>A UTF-8 string.</summary>
/// <param name="Value">The text, without its terminating NUL.</param>
/// <remarks>
/// An embedded NUL is refused rather than written. The wire form is NUL-terminated, so a string
/// carrying one comes back truncated at it: the value would not survive its own round trip, and
/// every reader downstream of the daemon sees a different string than the one that was sent.
/// </remarks>
/// <exception cref="ArgumentNullException"><paramref name="Value"/> is null.</exception>
/// <exception cref="ArgumentException"><paramref name="Value"/> contains a NUL.</exception>
public sealed record SpaString(string Value) : SpaValue
{
    /// <summary>The text, without its terminating NUL.</summary>
    public string Value { get; init; } = Validated(Value);

    /// <inheritdoc/>
    public override SpaType Type => SpaType.String;

    private static string Validated(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "a SPA string is NUL-terminated on the wire, so it cannot contain a NUL.",
                nameof(value));
        }

        return value;
    }
}

/// <summary>An opaque byte sequence.</summary>
/// <param name="Value">The bytes.</param>
public sealed record SpaBytes(ImmutableArray<byte> Value) : SpaValue
{
    /// <inheritdoc/>
    public override SpaType Type => SpaType.Bytes;

    /// <inheritdoc/>
    public bool Equals(SpaBytes? other) => SpaValueEquality.SequenceEqual(Value, other?.Value);

    /// <inheritdoc/>
    public override int GetHashCode() => SpaValueEquality.Combine(Value);
}

/// <summary>A width and height.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public sealed record SpaRectangle(uint Width, uint Height) : SpaValue
{
    /// <inheritdoc/>
    public override SpaType Type => SpaType.Rectangle;
}

/// <summary>A ratio. Frame rates are these.</summary>
/// <param name="Numerator">The numerator.</param>
/// <param name="Denominator">The denominator. Zero means unspecified rather than a division by zero.</param>
public sealed record SpaFraction(uint Numerator, uint Denominator) : SpaValue
{
    /// <inheritdoc/>
    public override SpaType Type => SpaType.Fraction;
}

/// <summary>A bitmap, one bit per pixel.</summary>
/// <param name="Bits">The raw bits.</param>
public sealed record SpaBitmap(ImmutableArray<byte> Bits) : SpaValue
{
    /// <inheritdoc/>
    public override SpaType Type => SpaType.Bitmap;

    /// <inheritdoc/>
    public bool Equals(SpaBitmap? other) => SpaValueEquality.SequenceEqual(Bits, other?.Bits);

    /// <inheritdoc/>
    public override int GetHashCode() => SpaValueEquality.Combine(Bits);
}

/// <summary>Several values that all share one type and size.</summary>
/// <param name="ChildType">The type every item has.</param>
/// <param name="Items">The items.</param>
/// <exception cref="ArgumentException">An item is not of <paramref name="ChildType"/>.</exception>
public sealed record SpaArray(SpaType ChildType, ImmutableArray<SpaValue> Items) : SpaValue
{
    /// <summary>The items.</summary>
    public ImmutableArray<SpaValue> Items { get; init; } = Coherent(ChildType, Items, nameof(Items));

    /// <inheritdoc/>
    public override SpaType Type => SpaType.Array;

    /// <summary>Checks that every child really is the declared type.</summary>
    /// <remarks>
    /// The wire format stores these children bare, with one size and one type for all of them, so a
    /// mismatch is not a value the format can express. Caught here rather than at write time, where
    /// the item is truncated or padded to fit and the failure is a wrong value rather than an error.
    /// </remarks>
    internal static ImmutableArray<SpaValue> Coherent(
        SpaType childType, ImmutableArray<SpaValue> items, string parameterName)
    {
        if (items.IsDefaultOrEmpty) return items;

        foreach (SpaValue item in items)
        {
            if (item.Type == childType) continue;

            throw new ArgumentException(
                $"every child must be {childType}; found a {item.Type}.", parameterName);
        }

        return items;
    }

    /// <inheritdoc/>
    public bool Equals(SpaArray? other) =>
        other is not null && ChildType == other.ChildType
        && SpaValueEquality.SequenceEqual(Items, other.Items);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(ChildType, SpaValueEquality.Combine(Items));
}

/// <summary>Several values that need not share a type.</summary>
/// <param name="Fields">The fields, in wire order.</param>
public sealed record SpaStruct(ImmutableArray<SpaValue> Fields) : SpaValue
{
    /// <inheritdoc/>
    public override SpaType Type => SpaType.Struct;

    /// <inheritdoc/>
    public bool Equals(SpaStruct? other) => SpaValueEquality.SequenceEqual(Fields, other?.Fields);

    /// <inheritdoc/>
    public override int GetHashCode() => SpaValueEquality.Combine(Fields);
}

/// <summary>One property of a <see cref="SpaObject"/>.</summary>
/// <param name="Key">
/// The property key, given as whichever enum names it for this object type - <see cref="SpaProp"/>
/// for <see cref="SpaParamType.Props"/>, <see cref="SpaFormat"/> for a format - or as a plain
/// number for a key this library has no name for.
/// </param>
/// <param name="Flags">Property flags, such as <see cref="SpaPodPropFlag.DontFixate"/>.</param>
/// <param name="Value">The property's value.</param>
public sealed record SpaProperty(SpaKey Key, uint Flags, SpaValue Value);

/// <summary>A keyed property bag. Every parameter is one of these.</summary>
/// <param name="ObjectType">What the object describes, such as <see cref="SpaType.ObjectProps"/>.</param>
/// <param name="ObjectId">Which parameter it answers.</param>
/// <param name="Properties">The properties, in wire order.</param>
public sealed record SpaObject(SpaType ObjectType, SpaParamType ObjectId, ImmutableArray<SpaProperty> Properties) : SpaValue
{
    /// <inheritdoc/>
    public override SpaType Type => SpaType.Object;

    /// <summary>The value of the property with this key, or <see langword="null"/> if absent.</summary>
    /// <param name="key">
    /// The property key, given as whichever enum names it for this object type - or as a plain
    /// number for a key this library has no name for.
    /// </param>
    public SpaValue? this[SpaKey key] => Find(key)?.Value;

    /// <summary>The property with this key, or <see langword="null"/> if absent.</summary>
    /// <param name="key">The property key.</param>
    /// <remarks>A daemon may repeat a key; the first wins, as it does in SPA's own parser.</remarks>
    public SpaProperty? Find(SpaKey key)
    {
        foreach (SpaProperty property in Properties)
        {
            if (property.Key == key)
                return property;
        }

        return null;
    }

    /// <inheritdoc/>
    public bool Equals(SpaObject? other) =>
        other is not null && ObjectType == other.ObjectType && ObjectId == other.ObjectId
        && SpaValueEquality.SequenceEqual(Properties, other.Properties);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(ObjectType, ObjectId, SpaValueEquality.Combine(Properties));
}

/// <summary>Several alternatives for one value: an enumeration, a range, or a flag set.</summary>
/// <param name="Kind">How the alternatives are to be read.</param>
/// <param name="ChildType">The type every alternative has.</param>
/// <param name="Alternatives">
/// The alternatives. Whatever the kind, the first is the default or current value - for a
/// <see cref="SpaChoiceType.Range"/> the two after it are the minimum and maximum.
/// </param>
/// <exception cref="ArgumentException">An alternative is not of <paramref name="ChildType"/>.</exception>
public sealed record SpaChoice(SpaChoiceType Kind, SpaType ChildType, ImmutableArray<SpaValue> Alternatives) : SpaValue
{
    /// <summary>The alternatives.</summary>
    public ImmutableArray<SpaValue> Alternatives { get; init; } =
        Arity(Kind, SpaArray.Coherent(ChildType, Alternatives, nameof(Alternatives)));

    /// <summary>Checks the count against what the kind means.</summary>
    /// <remarks>
    /// The kind is what tells a reader how to interpret the positions: Range is default, min, max
    /// and Step adds a step, so a Range holding two entries has no min or no max and there is no way
    /// to tell which. Nothing downstream can detect that - the daemon reads position 1 as the
    /// minimum whatever is there - so the count is checked where the caller is still in scope.
    /// </remarks>
    /// <summary>How many values the kind means, or 0 for the kinds that are lists.</summary>
    internal static int RequiredCount(SpaChoiceType kind) => kind switch
    {
        SpaChoiceType.None => 1,
        SpaChoiceType.Range => 3,
        SpaChoiceType.Step => 4,
        _ => 0,
    };

    /// <summary>Whether a count is one the kind can mean. The parser's form of the check.</summary>
    internal static bool CountFitsKind(SpaChoiceType kind, int count)
    {
        int required = RequiredCount(kind);
        return required == 0 || count == 0 || count == required;
    }

    private static ImmutableArray<SpaValue> Arity(
        SpaChoiceType kind, ImmutableArray<SpaValue> alternatives)
    {
        if (alternatives.IsDefaultOrEmpty) return alternatives;
        if (CountFitsKind(kind, alternatives.Length)) return alternatives;

        throw new ArgumentException(
            $"a {kind} choice carries exactly {RequiredCount(kind)} values; this one has "
            + $"{alternatives.Length}.",
            nameof(alternatives));
    }

    /// <inheritdoc/>
    public override SpaType Type => SpaType.Choice;

    /// <summary>The default or current value, or <see langword="null"/> when the choice is empty.</summary>
    public SpaValue? Default => Alternatives.IsDefaultOrEmpty ? null : Alternatives[0];

    /// <inheritdoc/>
    public bool Equals(SpaChoice? other) =>
        other is not null && Kind == other.Kind && ChildType == other.ChildType
        && SpaValueEquality.SequenceEqual(Alternatives, other.Alternatives);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(Kind, ChildType, SpaValueEquality.Combine(Alternatives));
}

/// <summary>One timed entry in a <see cref="SpaSequence"/>.</summary>
/// <param name="Offset">When it applies, in the sequence's unit.</param>
/// <param name="Type">What kind of control it is.</param>
/// <param name="Value">The value to apply.</param>
public sealed record SpaControl(uint Offset, uint Type, SpaValue Value);

/// <summary>A timed sequence of control values.</summary>
/// <param name="Unit">The unit offsets are expressed in.</param>
/// <param name="Controls">The entries, in time order.</param>
public sealed record SpaSequence(uint Unit, ImmutableArray<SpaControl> Controls) : SpaValue
{
    /// <inheritdoc/>
    public override SpaType Type => SpaType.Sequence;

    /// <inheritdoc/>
    public bool Equals(SpaSequence? other) =>
        other is not null && Unit == other.Unit
        && SpaValueEquality.SequenceEqual(Controls, other.Controls);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Unit, SpaValueEquality.Combine(Controls));
}

/// <summary>A pointer, meaningful only inside the process that wrote it.</summary>
/// <param name="PointerType">What is pointed at.</param>
/// <param name="Address">The address as written. This library never dereferences it.</param>
/// <remarks>
/// Not transportable. The address means something only in the address space that produced it, and
/// its width follows the writer's word size, so a pod carrying one cannot be forwarded to another
/// process or read correctly across a 32/64-bit boundary. Parsed for completeness; never send one.
/// </remarks>
public sealed record SpaPointer(SpaType PointerType, ulong Address) : SpaValue
{
    /// <inheritdoc/>
    public override SpaType Type => SpaType.Pointer;
}

/// <summary>A file descriptor.</summary>
/// <param name="Value">The descriptor number in the receiving process.</param>
/// <remarks>
/// A number, not a handle. Descriptors travel out of band over the socket as SCM_RIGHTS and are
/// installed by the protocol before the pod is parsed; what the pod carries is the index the
/// receiver ended up with. This value owns nothing: do not close it, and do not expect it to mean
/// anything in a process that did not receive the message.
/// </remarks>
public sealed record SpaFd(long Value) : SpaValue
{
    /// <inheritdoc/>
    public override SpaType Type => SpaType.Fd;
}

/// <summary>A pod nested inside another.</summary>
/// <param name="Value">The nested value.</param>
public sealed record SpaNestedPod(SpaValue Value) : SpaValue
{
    /// <inheritdoc/>
    public override SpaType Type => SpaType.Pod;
}

/// <summary>A pod whose type this library does not model.</summary>
/// <param name="UnknownType">The type tag that was read.</param>
/// <param name="Body">The undecoded body, padding excluded.</param>
/// <remarks>
/// Parsing never fails merely because a type is unrecognised. A newer daemon may send something
/// this version predates, and rejecting it would throw away the surrounding object's properties
/// that are understood. The bytes are kept so the value can be written back unchanged.
/// </remarks>
public sealed record SpaUnknown(SpaType UnknownType, ImmutableArray<byte> Body) : SpaValue
{
    /// <inheritdoc/>
    public override SpaType Type => UnknownType;

    /// <inheritdoc/>
    public bool Equals(SpaUnknown? other) =>
        other is not null && UnknownType == other.UnknownType
        && SpaValueEquality.SequenceEqual(Body, other.Body);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(UnknownType, SpaValueEquality.Combine(Body));
}
