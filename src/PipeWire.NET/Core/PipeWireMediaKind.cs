namespace PipeWire.NET;

/// <summary>What kind of media a node carries.</summary>
public enum PipeWireMediaKind
{
    /// <summary>No <c>media.class</c>, or one this library does not recognise.</summary>
    Unknown,

    /// <summary>Audio media, from a media.class starting with Audio/.</summary>
    Audio,

    /// <summary>Video media, from a media.class starting with Video/.</summary>
    Video,

    /// <summary>MIDI media, from a media.class starting with Midi/.</summary>
    Midi,
}
