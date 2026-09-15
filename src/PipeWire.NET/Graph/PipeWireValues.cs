namespace PipeWire.NET.Graph;

/// <summary>
/// Property values and factory names this library writes, as UTF-8.
/// </summary>
/// <remarks>
/// <para>
/// The keys these accompany are generated, because every one of them is a <c>PW_KEY_*</c> or
/// <c>SPA_KEY_*</c> macro in an installed header. Their values are not. PipeWire spells a factory
/// name and a media class as a bare string at the call site, and where a name is defined at all it
/// is a private <c>#define NAME</c> inside the module's own <c>.c</c> file - nothing a generator
/// pointed at the public headers can see. Spelling each one once here is the closest equivalent.
/// </para>
/// <para>
/// A <c>u8</c> literal's representation is NUL-terminated beyond its logical length, so taking a
/// pointer to one already satisfies libpipewire's C-string contract.
/// </para>
/// </remarks>
internal static class PipeWireValues
{
    /// <summary>The value <c>spa_atob</c> reads as true. It also accepts <c>"1"</c>.</summary>
    public static ReadOnlySpan<byte> True => "true"u8;

    /// <summary>The factory behind a virtual sink, from <c>module-null-audio-sink</c>.</summary>
    public static ReadOnlySpan<byte> NullAudioSink => "support.null-audio-sink"u8;

    /// <summary>The factory that wraps a node in format conversion, from <c>module-adapter</c>.</summary>
    public static ReadOnlySpan<byte> Adapter => "adapter"u8;

    /// <summary>The factory that creates links, from <c>module-link-factory</c>.</summary>
    public static ReadOnlySpan<byte> LinkFactory => "link-factory"u8;

    /// <summary>The <c>media.class</c> of a node that accepts audio.</summary>
    public static ReadOnlySpan<byte> AudioSink => "Audio/Sink"u8;

    /// <summary>An <c>audio.position</c> naming the two channels of a stereo pair.</summary>
    public static ReadOnlySpan<byte> StereoPosition => "[ FL FR ]"u8;
}
