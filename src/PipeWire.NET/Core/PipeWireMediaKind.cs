namespace PipeWire.NET;

/// <summary>What kind of media a node carries.</summary>
public enum PipeWireMediaKind
{
    /// <summary>No <c>media.class</c>, or one this library does not recognise.</summary>
    Unknown,

    /// <summary>Audio.</summary>
    Audio,

    /// <summary>Video.</summary>
    Video,

    /// <summary>MIDI.</summary>
    Midi,
}
