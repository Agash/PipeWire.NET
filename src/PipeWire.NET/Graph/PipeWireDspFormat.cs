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
}
