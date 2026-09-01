namespace PipeWire.NET;

/// <summary>
/// Parses PipeWire's <c>media.class</c> into the two orthogonal facts it encodes.
/// </summary>
/// <remarks>
/// <para>
/// <c>media.class</c> is a free-form, slash-separated string and the set of values grows. Parsing it
/// structurally rather than matching a fixed list means an unseen value still yields whatever it
/// does say: <c>Audio/Duplex</c> is recognised as audio even though nothing enumerates it, and a
/// future <c>Video/Duplex</c> would be too.
/// </para>
/// <para>
/// This answers <em>what a node is</em>, never <em>what you can do with it</em>. Whether media can
/// be captured from a node is a property of its ports, which the graph knows exactly - see
/// <c>PipeWireGraphSnapshot.CanCaptureFrom</c>. An audio sink is the case that makes the difference
/// obvious: it is a <see cref="PipeWireMediaFlow.Sink"/>, and it is also capturable, through the
/// monitor ports it exposes.
/// </para>
/// </remarks>
public static class PipeWireMediaClass
{
    /// <summary>The kind of media a <c>media.class</c> string describes.</summary>
    public static PipeWireMediaKind ParseKind(string? mediaClass)
    {
        ReadOnlySpan<char> s = mediaClass;
        if (s.IsEmpty) return PipeWireMediaKind.Unknown;

        // "Stream/Output/Audio" names its medium last; everything else names it first.
        ReadOnlySpan<char> head = NextSegment(ref s);
        if (head.Equals("Stream", StringComparison.Ordinal))
        {
            _ = NextSegment(ref s);              // Output | Input
            head = NextSegment(ref s);
        }

        if (head.Equals("Audio", StringComparison.Ordinal)) return PipeWireMediaKind.Audio;
        if (head.Equals("Video", StringComparison.Ordinal)) return PipeWireMediaKind.Video;
        if (head.Equals("Midi", StringComparison.Ordinal)) return PipeWireMediaKind.Midi;
        return PipeWireMediaKind.Unknown;
    }

    /// <summary>The direction a <c>media.class</c> string describes, relative to the graph.</summary>
    public static PipeWireMediaFlow ParseFlow(string? mediaClass)
    {
        ReadOnlySpan<char> s = mediaClass;
        if (s.IsEmpty) return PipeWireMediaFlow.Unknown;

        ReadOnlySpan<char> head = NextSegment(ref s);

        if (head.Equals("Stream", StringComparison.Ordinal))
        {
            // An app's Output is the graph's Source, and vice versa.
            ReadOnlySpan<char> dir = NextSegment(ref s);
            if (dir.Equals("Output", StringComparison.Ordinal)) return PipeWireMediaFlow.Source;
            if (dir.Equals("Input", StringComparison.Ordinal)) return PipeWireMediaFlow.Sink;
            return PipeWireMediaFlow.Unknown;
        }

        // "Audio/Source", "Audio/Source/Virtual", "Video/Sink", "Midi/Bridge", ...
        ReadOnlySpan<char> role = NextSegment(ref s);
        if (role.Equals("Source", StringComparison.Ordinal)) return PipeWireMediaFlow.Source;
        if (role.Equals("Sink", StringComparison.Ordinal)) return PipeWireMediaFlow.Sink;
        if (role.Equals("Duplex", StringComparison.Ordinal)) return PipeWireMediaFlow.Duplex;
        if (role.Equals("Bridge", StringComparison.Ordinal)) return PipeWireMediaFlow.Duplex;
        return PipeWireMediaFlow.Unknown;
    }

    /// <summary>Takes the next <c>/</c>-separated segment, advancing <paramref name="rest"/>.</summary>
    private static ReadOnlySpan<char> NextSegment(ref ReadOnlySpan<char> rest)
    {
        if (rest.IsEmpty) return default;

        int slash = rest.IndexOf('/');
        if (slash < 0)
        {
            ReadOnlySpan<char> all = rest;
            rest = default;
            return all;
        }

        ReadOnlySpan<char> segment = rest[..slash];
        rest = rest[(slash + 1)..];
        return segment;
    }
}
