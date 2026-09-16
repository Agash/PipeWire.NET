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

    /// <summary>A key naming part of a parameter dictionary, such as a stream Capability.</summary>
    /// <param name="key">The key.</param>
    public static implicit operator SpaKey(SpaParamDict key) => new((uint)key);

    /// <inheritdoc/>
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
