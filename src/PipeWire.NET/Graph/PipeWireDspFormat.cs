namespace PipeWire.NET.Graph;

/// <summary>What a filter port carries.</summary>
/// <remarks>
/// The graph's DSP links carry exactly these three shapes, spelled as the <c>format.dsp</c> property
/// (<c>pipewire/keys.h:369</c>). A port declaring anything else is not linkable to the rest of the
/// graph, which is why this is an enum rather than the string it becomes.
/// </remarks>
public enum PipeWireDspFormat
{
    /// <summary>One channel of 32 bit float samples. What an audio filter port carries.</summary>
    /// <remarks>
    /// Mono per port by design: a stereo filter has two ports, not one port with two channels, which
    /// is what lets the graph route each channel independently.
    /// </remarks>
    MonoAudio = 0,

    /// <summary>Timed MIDI events, as a sequence of controls per buffer rather than samples.</summary>
    Midi = 1,

    /// <summary>Timed control values, in the same per-buffer sequence shape as MIDI.</summary>
    Control = 2,

    /// <summary>
    /// One frame of 32 bit float RGBA pixels. What a video filter port carries.
    /// </summary>
    /// <remarks>
    /// The video counterpart of <see cref="MonoAudio"/>, and the format upstream's
    /// <c>video-dsp-play</c> and <c>video-dsp-src</c> examples declare. Unlike the audio case a
    /// video port carries all four channels together, because a frame is not routed per channel;
    /// the geometry comes from the graph's position area rather than from the port.
    /// </remarks>
    Rgba32FloatVideo = 3,

    /// <summary>
    /// Timed MIDI 2.0 events as Universal MIDI Packets, rather than the 8-bit byte stream.
    /// </summary>
    /// <remarks>
    /// The fifth format <c>pw_filter</c> accepts, and the only one that changes the port's own
    /// properties: PipeWire sets <c>control.ump</c> and rewrites <c>format.dsp</c> back to
    /// <c>8 bit raw midi</c>, so a port created as UMP reports itself as MIDI afterwards. The
    /// difference is in the control type the port carries, not in the name it ends up with.
    /// </remarks>
    Ump = 4,
}
