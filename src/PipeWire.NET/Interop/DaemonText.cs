using System.Runtime.Versioning;
using System.Text;

namespace PipeWire.NET.Interop;

/// <summary>
/// Decodes NUL-terminated strings that arrived from the daemon.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these is a pointer into memory this process did not allocate and does not know the
/// length of. <see cref="System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpanFromNullTerminated(byte*)"/>
/// and <c>Marshal.PtrToStringUTF8</c> both scan until they find a zero byte, so a string the daemon
/// forgot to terminate is read past its allocation until one turns up.
/// </para>
/// <para>
/// The cap here is not a length limit on anything real. The longest strings that cross this
/// boundary are property values and error messages, both far below it; it exists so that a
/// malformed pointer is a truncated string rather than a walk through unmapped memory.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal static unsafe class DaemonText
{
    /// <summary>How far a scan for the terminator will go before giving up.</summary>
    internal const int MaxBytes = 64 * 1024;

    /// <summary>The bytes up to the terminator, or up to <see cref="MaxBytes"/>.</summary>
    /// <param name="text">A NUL-terminated pointer, or null.</param>
    internal static ReadOnlySpan<byte> Bytes(sbyte* text)
    {
        if (text is null) return default;

        var bytes = (byte*)text;
        int length = 0;
        while (length < MaxBytes && bytes[length] != 0)
            length++;

        return new ReadOnlySpan<byte>(bytes, length);
    }

    /// <summary>The decoded string, or <see langword="null"/> when the pointer is null.</summary>
    /// <param name="text">A NUL-terminated pointer, or null.</param>
    internal static string? String(sbyte* text) =>
        text is null ? null : Encoding.UTF8.GetString(Bytes(text));
}
