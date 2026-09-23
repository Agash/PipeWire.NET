namespace PipeWire.NET.Spa;

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
    public unsafe TEnum As<TEnum>()
        where TEnum : unmanaged, Enum
    {
        if (sizeof(TEnum) != sizeof(uint))
        {
            throw new ArgumentException(
                $"{typeof(TEnum).Name} is {sizeof(TEnum)} bytes; a SPA id enum is four. "
                    + "This id does not belong to that enum.",
                nameof(TEnum)
            );
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
    public override string ToString() =>
        Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
