namespace PipeWire.NET.Spa;

/// <summary>
/// The key of a property inside a SPA object.
/// </summary>
/// <remarks>
/// Which enum a key comes from depends on what the object describes - <see cref="SpaFormat"/> for a
/// format, <see cref="SpaProp"/> for a node's controls, <see cref="SpaParamRoute"/> for a device
/// route - so no single enum can type the parameter. This converts from all of them implicitly,
/// which is what lets a caller write the key it means without casting it to a number first.
/// </remarks>
public readonly record struct SpaKey
{
    /// <summary>The numeric key as it goes on the wire.</summary>
    public uint Value { get; }

    private SpaKey(uint value) => Value = value;

    /// <summary>Wraps a key whose enum this library does not model.</summary>
    /// <param name="value">The numeric key.</param>
    public static SpaKey FromRaw(uint value) => new(value);

    /// <summary>The numeric key.</summary>
    /// <param name="key">The key.</param>
    public static implicit operator uint(SpaKey key) => key.Value;

    /// <summary>A key given as a plain number, for one this library has no enum for.</summary>
    /// <param name="value">The numeric key.</param>
    public static implicit operator SpaKey(uint value) => new(value);

    /// <summary>Reads the key as the enum the enclosing object type is documented to use.</summary>
    /// <typeparam name="TEnum">The enum to read it as; it must be four bytes wide, as every SPA enum is.</typeparam>
    /// <exception cref="ArgumentException">
    /// <typeparamref name="TEnum"/> is not four bytes wide, so it is not a SPA key enum.
    /// </exception>
    /// <remarks>
    /// The width cannot be constrained at compile time, and the reinterpret it does otherwise
    /// throws a <see cref="NotSupportedException"/> naming neither type. Since the size is a
    /// constant for any given <typeparamref name="TEnum"/>, the check costs nothing once inlined.
    /// </remarks>
    public unsafe TEnum As<TEnum>() where TEnum : unmanaged, Enum
    {
        if (sizeof(TEnum) != sizeof(uint))
        {
            throw new ArgumentException(
                $"{typeof(TEnum).Name} is {sizeof(TEnum)} bytes; a SPA key enum is four. "
                + "This key does not belong to that enum.", nameof(TEnum));
        }

        return System.Runtime.CompilerServices.Unsafe.BitCast<uint, TEnum>(Value);
    }

    /// <summary>A key naming part of a media format.</summary>
    /// <param name="key">The key.</param>
    public static implicit operator SpaKey(SpaFormat key) => new((uint)key);

    /// <summary>A key naming part of a node property.</summary>
    /// <param name="key">The key.</param>
    public static implicit operator SpaKey(SpaProp key) => new((uint)key);

    /// <summary>A key naming part of a property description.</summary>
    /// <param name="key">The key.</param>
    public static implicit operator SpaKey(SpaPropInfo key) => new((uint)key);

    /// <summary>A key naming part of buffer requirements.</summary>
    /// <param name="key">The key.</param>
    public static implicit operator SpaKey(SpaParamBuffers key) => new((uint)key);

    /// <summary>A key naming part of buffer metadata.</summary>
    /// <param name="key">The key.</param>
    public static implicit operator SpaKey(SpaParamMeta key) => new((uint)key);

    /// <summary>A key naming part of a device route.</summary>
    /// <param name="key">The key.</param>
    public static implicit operator SpaKey(SpaParamRoute key) => new((uint)key);

    /// <summary>A key naming part of a device profile.</summary>
    /// <param name="key">The key.</param>
    public static implicit operator SpaKey(SpaParamProfile key) => new((uint)key);

    /// <summary>A key naming part of a port arrangement.</summary>
    /// <param name="key">The key.</param>
    public static implicit operator SpaKey(SpaParamPortConfig key) => new((uint)key);

    /// <summary>A key naming part of reported latency.</summary>
    /// <param name="key">The key.</param>
    public static implicit operator SpaKey(SpaParamLatency key) => new((uint)key);

    /// <summary>A key naming part of processing latency.</summary>
    /// <param name="key">The key.</param>
    public static implicit operator SpaKey(SpaParamProcessLatency key) => new((uint)key);

    /// <summary>A key naming part of a stream tag.</summary>
    /// <param name="key">The key.</param>
    public static implicit operator SpaKey(SpaParamTag key) => new((uint)key);

    /// <summary>A key naming part of an IO area.</summary>
    /// <param name="key">The key.</param>
    public static implicit operator SpaKey(SpaParamIo key) => new((uint)key);

    /// <summary>A key naming part of profiling data.</summary>
    /// <param name="key">The key.</param>
    public static implicit operator SpaKey(SpaProfiler key) => new((uint)key);

    /// <inheritdoc/>
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The value of a SPA id: an enumeration member rather than a number.
/// </summary>
/// <remarks>
/// Ids are how SPA names a pixel format, a channel, a direction or a buffer type. Like
/// <see cref="SpaKey"/> the enum it belongs to depends on the property carrying it, so this
/// converts from all of them implicitly and callers never cast.
/// </remarks>
public readonly record struct SpaIdValue
{
    /// <summary>The numeric id as it goes on the wire.</summary>
    public uint Value { get; }

    private SpaIdValue(uint value) => Value = value;

    /// <summary>Wraps an id whose enum this library does not model.</summary>
    /// <param name="value">The numeric id.</param>
    public static SpaIdValue FromRaw(uint value) => new(value);

    /// <summary>The numeric id.</summary>
    /// <param name="id">The id.</param>
    public static implicit operator uint(SpaIdValue id) => id.Value;

    /// <summary>Reads the id as the enum the property carrying it is documented to use.</summary>
    /// <typeparam name="TEnum">The enum to read it as; it must be four bytes wide, as every SPA enum is.</typeparam>
    /// <exception cref="ArgumentException">
    /// <typeparamref name="TEnum"/> is not four bytes wide, so it is not a SPA id enum.
    /// </exception>
    /// <remarks>
    /// The value comes from the daemon, so it may name a member this version of the enum does not
    /// have. That is not an error - it is a newer PipeWire - and the result compares equal to
    /// nothing rather than throwing. A wrongly sized enum is a different matter and is a caller
    /// error, reported as one here rather than as a NotSupportedException out of the reinterpret
    /// that names neither type.
    /// </remarks>
    public unsafe TEnum As<TEnum>() where TEnum : unmanaged, Enum
    {
        if (sizeof(TEnum) != sizeof(uint))
        {
            throw new ArgumentException(
                $"{typeof(TEnum).Name} is {sizeof(TEnum)} bytes; a SPA id enum is four. "
                + "This id does not belong to that enum.", nameof(TEnum));
        }

        return System.Runtime.CompilerServices.Unsafe.BitCast<uint, TEnum>(Value);
    }

    /// <summary>An id drawn from <see cref="SpaType"/>.</summary>
    /// <param name="id">The id.</param>
    public static implicit operator SpaIdValue(SpaType id) => new((uint)id);

    /// <summary>An id drawn from <see cref="SpaParamType"/>.</summary>
    /// <param name="id">The id.</param>
    public static implicit operator SpaIdValue(SpaParamType id) => new((uint)id);

    /// <summary>An id drawn from <see cref="SpaMediaType"/>.</summary>
    /// <param name="id">The id.</param>
    public static implicit operator SpaIdValue(SpaMediaType id) => new((uint)id);

    /// <summary>An id drawn from <see cref="SpaMediaSubtype"/>.</summary>
    /// <param name="id">The id.</param>
    public static implicit operator SpaIdValue(SpaMediaSubtype id) => new((uint)id);

    /// <summary>An id drawn from <see cref="SpaVideoFormat"/>.</summary>
    /// <param name="id">The id.</param>
    public static implicit operator SpaIdValue(SpaVideoFormat id) => new((uint)id);

    /// <summary>An id drawn from <see cref="SpaAudioFormat"/>.</summary>
    /// <param name="id">The id.</param>
    public static implicit operator SpaIdValue(SpaAudioFormat id) => new((uint)id);

    /// <summary>An id drawn from <see cref="SpaAudioChannel"/>.</summary>
    /// <param name="id">The id.</param>
    public static implicit operator SpaIdValue(SpaAudioChannel id) => new((uint)id);

    /// <summary>An id drawn from <see cref="SpaMetaType"/>.</summary>
    /// <param name="id">The id.</param>
    public static implicit operator SpaIdValue(SpaMetaType id) => new((uint)id);

    /// <summary>An id drawn from <see cref="SpaDataType"/>.</summary>
    /// <param name="id">The id.</param>
    public static implicit operator SpaIdValue(SpaDataType id) => new((uint)id);

    /// <summary>An id drawn from <see cref="SpaDirection"/>.</summary>
    /// <param name="id">The id.</param>
    public static implicit operator SpaIdValue(SpaDirection id) => new((uint)id);

    /// <summary>An id drawn from <see cref="SpaVideoColorRange"/>.</summary>
    /// <param name="id">The id.</param>
    public static implicit operator SpaIdValue(SpaVideoColorRange id) => new((uint)id);

    /// <summary>An id drawn from <see cref="SpaVideoColorMatrix"/>.</summary>
    /// <param name="id">The id.</param>
    public static implicit operator SpaIdValue(SpaVideoColorMatrix id) => new((uint)id);

    /// <summary>An id drawn from <see cref="SpaVideoColorPrimaries"/>.</summary>
    /// <param name="id">The id.</param>
    public static implicit operator SpaIdValue(SpaVideoColorPrimaries id) => new((uint)id);

    /// <summary>An id drawn from <see cref="SpaVideoTransferFunction"/>.</summary>
    /// <param name="id">The id.</param>
    public static implicit operator SpaIdValue(SpaVideoTransferFunction id) => new((uint)id);

    /// <summary>An id drawn from <see cref="SpaVideoInterlaceMode"/>.</summary>
    /// <param name="id">The id.</param>
    public static implicit operator SpaIdValue(SpaVideoInterlaceMode id) => new((uint)id);

    /// <summary>An id drawn from <see cref="SpaVideoChromaSite"/>.</summary>
    /// <param name="id">The id.</param>
    public static implicit operator SpaIdValue(SpaVideoChromaSite id) => new((uint)id);

    /// <summary>An id drawn from <see cref="SpaParamAvailability"/>.</summary>
    /// <param name="id">The id.</param>
    public static implicit operator SpaIdValue(SpaParamAvailability id) => new((uint)id);

    /// <summary>An id drawn from <see cref="SpaParamPortConfigMode"/>.</summary>
    /// <param name="id">The id.</param>
    public static implicit operator SpaIdValue(SpaParamPortConfigMode id) => new((uint)id);

    /// <summary>An id drawn from <see cref="SpaParamBitorder"/>.</summary>
    /// <param name="id">The id.</param>
    public static implicit operator SpaIdValue(SpaParamBitorder id) => new((uint)id);

    /// <summary>An id drawn from <see cref="SpaAudioVolumeRampScale"/>.</summary>
    /// <param name="id">The id.</param>
    public static implicit operator SpaIdValue(SpaAudioVolumeRampScale id) => new((uint)id);

    /// <summary>An id drawn from <see cref="SpaIoType"/>.</summary>
    /// <param name="id">The id.</param>
    public static implicit operator SpaIdValue(SpaIoType id) => new((uint)id);

    /// <summary>An id drawn from <see cref="SpaMetaVideotransformValue"/>.</summary>
    /// <param name="id">The id.</param>
    public static implicit operator SpaIdValue(SpaMetaVideotransformValue id) => new((uint)id);

    /// <inheritdoc/>
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
