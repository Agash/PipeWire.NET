using System.Collections.Immutable;
using System.Drawing;

namespace PipeWire.NET.Portal;

/// <summary>What a screen cast may share (the portal's <c>types</c> and <c>AvailableSourceTypes</c>).</summary>
[Flags]
public enum ScreenCastSources : uint
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Existing monitors.</summary>
    Monitor = 1,

    /// <summary>Application windows.</summary>
    Window = 2,

    /// <summary>A new virtual monitor that extends the desktop.</summary>
    Virtual = 4,
}

/// <summary>How the cursor reaches the stream (the portal's <c>cursor_mode</c> and <c>AvailableCursorModes</c>).</summary>
[Flags]
public enum ScreenCastCursorModes : uint
{
    /// <summary>None stated.</summary>
    None = 0,

    /// <summary>The cursor is not part of the stream.</summary>
    Hidden = 1,

    /// <summary>The cursor is drawn into the stream's buffers.</summary>
    Embedded = 2,

    /// <summary>The cursor is sent as PipeWire stream metadata (<c>SPA_META_Cursor</c>), apart from the image.</summary>
    Metadata = 4,
}

/// <summary>How long the user's choice is remembered (the portal's <c>persist_mode</c>).</summary>
public enum ScreenCastPersistence : uint
{
    /// <summary>Not remembered: the user chooses again next time.</summary>
    None = 0,

    /// <summary>Remembered while the application runs.</summary>
    WhileRunning = 1,

    /// <summary>Remembered until the user revokes it; restored with <see cref="ScreenCastSession.RestoreToken"/>.</summary>
    UntilRevoked = 2,
}

/// <summary>What to ask the user to share.</summary>
public sealed record ScreenCastRequest
{
    /// <summary>The kinds of source offered; monitors by default.</summary>
    public ScreenCastSources Sources { get; init; } = ScreenCastSources.Monitor;

    /// <summary>Whether the user may pick several sources, each a stream of its own.</summary>
    public bool Multiple { get; init; }

    /// <summary>
    /// How the cursor reaches the stream; null leaves the portal's default. It must be one of the
    /// portal's <see cref="ScreenCastPortal.AvailableCursorModes"/>.
    /// </summary>
    public ScreenCastCursorModes? CursorMode { get; init; }

    /// <summary>How long the choice is remembered. Needs version 4 of the portal.</summary>
    public ScreenCastPersistence Persistence { get; init; }

    /// <summary>
    /// A token from an earlier session's <see cref="ScreenCastSession.RestoreToken"/>, to share the same
    /// sources again without asking. Needs version 4 of the portal.
    /// </summary>
    public string? RestoreToken { get; init; }

    /// <summary>
    /// The window the dialog belongs to, in the portal's form (<c>x11:XID</c> or <c>wayland:HANDLE</c>);
    /// empty for none.
    /// </summary>
    public string ParentWindow { get; init; } = string.Empty;
}

/// <summary>One stream the user agreed to share.</summary>
/// <param name="NodeId">The PipeWire node to connect a stream to.</param>
public readonly record struct ScreenCastStream(uint NodeId)
{
    /// <summary>An identifier that stays the same across restored sessions; null before version 4.</summary>
    public string? Id { get; init; }

    /// <summary>Where the source is in the compositor's coordinate space; null when not known.</summary>
    public Point? Position { get; init; }

    /// <summary>The source's size in compositor coordinates; null when not known.</summary>
    public Size? Size { get; init; }

    /// <summary>The kind of source.</summary>
    public ScreenCastSources Source { get; init; }

    /// <summary>
    /// The identifier that ties the stream to input regions of a remote desktop session; null when the
    /// portal gives none.
    /// </summary>
    public string? MappingId { get; init; }

    /// <summary>
    /// The node's PipeWire object serial, which unlike the node id is never reused; null before version 6.
    /// </summary>
    public ulong? Serial { get; init; }
}

/// <summary>A screen cast session's streams.</summary>
/// <param name="Streams">The streams, one per source the user picked.</param>
/// <param name="RestoreToken">The token that restores the choice next time; null when not remembered.</param>
internal readonly record struct ScreenCastStart(
    ImmutableArray<ScreenCastStream> Streams,
    string? RestoreToken
);
