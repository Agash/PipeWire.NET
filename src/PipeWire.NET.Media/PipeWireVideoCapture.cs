using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Media;

/// <summary>
/// Receives video frames from a PipeWire source (V4L2 camera, virtual camera,
/// screen-capture portal node, or another app's video output).
/// </summary>
/// <remarks>
/// <see cref="FrameReady"/> fires on the PipeWire loop thread; the <see cref="VideoFrame"/>
/// is a <see langword="ref struct"/> whose data is valid only for the duration of the handler.
/// <para>
/// <b>Lifetime.</b> One connection per instance: <c>Connect</c> refuses a second call, and disposal
/// is final. To point at a different source, make a new instance. There is deliberately no
/// reconnect, because a reconnect that reuses the negotiated format and buffers of a stream that
/// already ended is a different object wearing the old one's state.
/// </para>
/// <para>
/// <b>What the daemon does when a source disappears</b> is a separate question, and by default it
/// attaches the stream to another one. That is convenient for a media player and wrong for anything
/// that cares which device it is reading: frames keep arriving, from somewhere else, with nothing
/// in the API to say so. Pass <c>stayWithTheSource</c> to end the stream instead.
/// </para>
/// <para>
/// <b>Dispose it; do not let it fall out of scope.</b> The callbacks the daemon holds refer back
/// here through a weak handle, so an instance the application drops is collected and simply stops
/// delivering, with no error and no final state change. That is the deliberate half of the trade:
/// a strong handle would keep every one ever made alive for the life of the process. What it costs
/// is that the garbage collector cannot be the thing that closes one, because by the time it runs
/// there is nothing left to close it from.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class PipeWireVideoCapture : IAsyncDisposable
{
    /// <summary>Wildcard node id - let PipeWire auto-select a source.</summary>
    public const uint AnyNode = NativeConstants.PW_ID_ANY;

    /// <summary>Handles a frame on the loop thread. Do not cache the frame.</summary>
    public delegate void FrameReadyHandler(PipeWireVideoCapture sender, VideoFrame frame);

    /// <summary>Handles a connection state change on the loop thread.</summary>
    public delegate void StateChangedHandler(PipeWireVideoCapture sender, PipeWireStreamState oldState, PipeWireStreamState newState);

    /// <summary>Raised on the loop thread when a frame is ready. Do not cache the frame.</summary>
    public event FrameReadyHandler? FrameReady;

    /// <summary>Raised on the loop thread when the connection state changes.</summary>
    public event StateChangedHandler? StateChanged;

    private readonly PipeWireContext _ctx;
    private readonly string _name;
    private readonly ILogger _logger;
    private PipeWireStreamCore? _core;
    private ulong _sequence;
    private PulledVideoFrame? _latest;
    private readonly Lock _borrowGate = new();
    private BorrowedVideoFrame _borrowed;
    private bool _hasBorrowed;

    // Explicit-sync releases owed for borrowed frames, under _borrowGate. The first belongs to the
    // frame waiting to be pulled, the second to the one a caller has pulled and not yet let go of.
    private SyncRelease _borrowedRelease;
    private SyncRelease _heldRelease;

    // How long a frame's acquire point is waited for before the frame is dropped as never ready.
    private static readonly TimeSpan AcquireTimeout = TimeSpan.FromSeconds(1);

    // Boxed and swapped whole rather than mutated in place. The format is written by the loop
    // thread in OnFormat and read from NegotiatedModifier on whatever thread the caller is on; a
    // multi-field struct has no atomic assignment, so a reader could otherwise see a width from one
    // negotiation next to a modifier from the next. Swapping a reference is atomic, and the cost is
    // one allocation per negotiation rather than one per frame.
    private sealed class NegotiatedFormat(SpaFormatPod.VideoFormatInfo info)
    {
        public SpaFormatPod.VideoFormatInfo Info { get; } = info;
    }

    private NegotiatedFormat _fmtCell =
        new(new SpaFormatPod.VideoFormatInfo(PixelFormat.Bgra, 0, 0, VideoColorInfo.Unknown));

    private SpaFormatPod.VideoFormatInfo Format => Volatile.Read(ref _fmtCell).Info;

    // Whether the caller opted into modifier negotiation, and the single pixel format the modifiers
    // apply to. We do NOT retain the offered modifier list: fixation re-offers the producer's preferred
    // returned modifier (carried as the scalar _fmt.Modifier), which is always within our offered set.
    private bool _modifiersOffered;
    private bool _explicitSyncRequested;
    private PixelFormat _modifierFormat;
    private bool _modifierFixated;

    // Device-ID negotiation: the offers, and the geometry the formats announced on the peer's
    // capabilities are written with. Empty when the stream was connected without device offers.
    private DmaBufDeviceOffer[] _deviceOffers = [];
    private bool _announcedToAPeer;
    private (uint Width, uint Height, uint FrameRate) _preferredGeometry;

    /// <summary>How many damage regions to make room for; more than this and the rest are dropped.</summary>
    private const int DamageRegions = 8;

    /// <summary>
    /// The DRM format modifier negotiated for delivered dmabuf frames, or
    /// <see cref="DrmFormatModifier.Invalid"/> when none (host-memory path or no modifier offered).
    /// </summary>
    public ulong NegotiatedModifier => Format.Modifier;

    /// <summary>
    /// The DRM device the frames were negotiated for, or null when there was no device negotiation.
    /// </summary>
    /// <remarks>
    /// Null until a format is settled; null for a stream connected without device offers; and null when
    /// the producer does not take part in device-ID negotiation, which is upstream's "device
    /// undefined" - import on the device you would have used without it.
    /// </remarks>
    public DrmDevice? NegotiatedDevice => DeviceIdNegotiation.Resolve(Format.DeviceId, _deviceOffers);

    /// <param name="context">A started <see cref="PipeWireContext"/>.</param>
    /// <param name="name">node.name advertised in the graph.</param>
    public PipeWireVideoCapture(PipeWireContext context, string name = "PipeWire.NET.VideoCapture")
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(name);
        _ctx = context;
        _name = name;
        _logger = context.LoggerFactory.CreateLogger($"PipeWire.NET.{name}");
    }

    /// <summary>Connects to a discovered source.</summary>
    /// <param name="source">The node to capture from.</param>
    /// <param name="preferredFormats">Preferred pixel formats in priority order.</param>
    /// <param name="cancellationToken">
    /// Abandons the wait for the loop lock. The connect request itself is issued
    /// synchronously once that is held, so there is nothing to recall after it.
    /// </param>
    public void Connect(
        PipeWireNode source,
        ReadOnlySpan<PixelFormat> preferredFormats = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        Connect(source.NodeId, preferredFormats, cancellationToken: cancellationToken);
    }

    /// <summary>Connects to a source by node id (default: auto-select).</summary>
    /// <param name="targetNodeId">Source node id, or <see cref="AnyNode"/> to auto-select.</param>
    /// <param name="preferredFormats">Preferred pixel formats in priority order.</param>
    /// <param name="stayWithTheSource">
    /// <see langword="true"/> to end the stream when its source goes away, rather than letting the
    /// daemon attach it to another one.
    /// </param>
    /// <param name="pullMode">
    /// Take the driver role, so cycles happen when this stream asks for them rather than when the
    /// graph schedules them. Pair with <see cref="TriggerProcess"/>, and with
    /// <see cref="CommandReceived"/> to answer <see cref="SpaNodeCommand.RequestProcess"/>. This is
    /// how upstream's pull example consumes: a driver on an input stream pulls.
    /// </param>
    /// <param name="preferredWidth">Preferred width in pixels.</param>
    /// <param name="preferredHeight">Preferred height in pixels.</param>
    /// <param name="preferredFrameRate">Preferred frame rate in frames per second.</param>
    /// <remarks>
    /// The geometry is a preference, not a demand: the offer accepts a range around it, so a
    /// producer of another size still negotiates and the frames that arrive may not match what was
    /// asked for. Read <see cref="VideoFrame.Width"/> and <see cref="VideoFrame.Height"/> rather
    /// than assuming. The defaults are what a consumer that does not care should ask for.
    /// </remarks>
    /// <param name="targetObjectName">
    /// Optional <c>target.object</c> - bind to a specific node by name/serial regardless of
    /// the session manager's default-device routing.
    /// </param>
    /// <param name="modifiers">
    /// DRM format modifiers to offer for a zero-copy dmabuf negotiation (the consumer's
    /// GPU-importable set). When non-empty, <paramref name="preferredFormats"/> must name exactly one
    /// format. The library auto-fixates to the producer's preferred modifier from this set.
    /// </param>
    /// <param name="requestExplicitSync">
    /// Ask the producer for <c>SPA_META_SyncTimeline</c>, so frames carry acquire and release points
    /// instead of relying on implicit fences. Only meaningful alongside
    /// <paramref name="modifiers"/>, and only worth asking for if the consumer is going to wait on
    /// <see cref="VideoFrame.SyncTimeline"/>: a producer that agrees to explicit sync stops
    /// attaching implicit fences, so ignoring the points races the GPU still writing the frame.
    /// </param>
    /// <param name="deviceOffers">
    /// The devices this consumer can import on, each with the modifiers it can import there, in
    /// priority order - PipeWire's DMA-BUF device-ID negotiation, as upstream's video-play-fixate does
    /// it. Instead of <paramref name="modifiers"/>, not with them, and with exactly one
    /// <paramref name="preferredFormats"/> entry. The stream connects inactive, learns the producer's
    /// capabilities, offers one format per device the producer can work with plus a host-memory
    /// fallback, and activates. <see cref="NegotiatedDevice"/> says which device was chosen; a producer
    /// that does not negotiate is offered the first device's modifiers without a device.
    /// </param>
    /// <param name="autoConnect">
    /// When true the session manager routes this stream. Pass <see langword="false"/> to publish
    /// the node unlinked and link it deliberately (<see cref="Graph.PipeWireRegistry.CreateLinkAsync(Graph.PipeWirePort, Graph.PipeWirePort, CancellationToken)"/>),
    /// out of the session manager's policy entirely: nothing moves it to another source when a
    /// linked one goes, and nothing links it to a default. What a patchbay or a router wants.
    /// </param>
    /// <param name="cancellationToken">
    /// Abandons the wait for the loop lock. The connect request itself is issued
    /// synchronously once that is held, so there is nothing to recall after it.
    /// </param>
    public unsafe void Connect(uint targetNodeId = AnyNode,
        ReadOnlySpan<PixelFormat> preferredFormats = default,
        string? targetObjectName = null,
        ReadOnlySpan<long> modifiers = default,
        bool stayWithTheSource = false,
        bool pullMode = false,
        int preferredWidth = 1920,
        int preferredHeight = 1080,
        int preferredFrameRate = 30,
        bool requestExplicitSync = false,
        ReadOnlySpan<DmaBufDeviceOffer> deviceOffers = default,
        bool autoConnect = true,
        CancellationToken cancellationToken = default)
    {
        if (_core is not null) throw new InvalidOperationException("Already connected.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preferredWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preferredHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preferredFrameRate);
        if (!deviceOffers.IsEmpty)
        {
            if (!modifiers.IsEmpty)
                throw new ArgumentException(
                    "Offer modifiers either alone or per device, not both: each device offer carries its own.",
                    nameof(modifiers));
            DeviceIdNegotiation.Validate(deviceOffers, nameof(deviceOffers));
        }

        bool dmaBuf = !modifiers.IsEmpty || !deviceOffers.IsEmpty;
        if (dmaBuf && preferredFormats.Length != 1)
            throw new ArgumentException(
                "Exactly one pixel format must be specified when offering DRM modifiers (modifiers are per-format).",
                nameof(preferredFormats));

        _modifiersOffered = dmaBuf;
        _explicitSyncRequested = requestExplicitSync && dmaBuf;
        _modifierFormat = preferredFormats.Length == 1 ? preferredFormats[0] : default;
        _modifierFixated = false;
        _deviceOffers = deviceOffers.ToArray();
        _announcedToAPeer = false;
        _preferredGeometry = ((uint)preferredWidth, (uint)preferredHeight, (uint)preferredFrameRate);

        var props = new StreamProperties(StreamMediaType.Video, StreamCategory.Capture)
            .WithRole("Camera")
            .WithNodeName(_name);

        // After the stream's own defaults, so an explicit media.role or node.description from the
        // caller wins rather than being silently ignored. An explicit method argument still beats
        // both, which is why target.object is applied below this.
        if (ExtraProperties is { Count: > 0 })
        {
            foreach (KeyValuePair<string, string> kv in ExtraProperties)
                props.With(kv.Key, kv.Value);
        }

        if (targetObjectName is not null) props.WithTargetObject(targetObjectName);
        // Built locally and only published once the connect succeeded. Assigning the field first
        // leaves a failed connect behind a stream that reports itself already connected and can
        // never be retried.

        bool negotiateDevices = !deviceOffers.IsEmpty;
        var core = new PipeWireStreamCore(_ctx, props, _name, OnBuffer, OnState, OnFormat, OnPostFormat,
            onPeerConnected: negotiateDevices ? OnPeerConnected : null);

        // With device offers the first pod is video-play-fixate's bare format - no modifiers, so a
        // host-memory shape - and the real offers go out once the producer's capabilities are known.
        Span<byte> pod = stackalloc byte[1024];
        int len = SpaFormatPod.WriteVideoFormat(pod, preferredFormats,
            (uint)preferredWidth, (uint)preferredHeight, (uint)preferredFrameRate, fixedSize: false,
            modifiers: modifiers);

        // A second offer with no modifiers, so a producer that cannot provide DMA-BUF has a
        // host-memory shape to agree to. Only when modifiers were asked for: without them the
        // first pod already is the host-memory offer and a duplicate says nothing.
        Span<byte> fallback = stackalloc byte[1024];
        int fallbackLen = modifiers.IsEmpty
            ? 0
            : SpaFormatPod.WriteVideoFormat(fallback, preferredFormats,
                (uint)preferredWidth, (uint)preferredHeight, (uint)preferredFrameRate, fixedSize: false);

        // A consumer names no devices in its Capability, as video-play-fixate does not: it filters
        // the producer's list instead.
        byte[] capability = negotiateDevices ? DeviceIdNegotiation.CapabilityParam([]) : [];

        try
        {
            core.Connect(SpaDirection.Input, targetNodeId,
            PipeWireStreamFlags.MapBuffers
                | (autoConnect ? PipeWireStreamFlags.Autoconnect : 0)
                | (stayWithTheSource ? PipeWireStreamFlags.DontReconnect : 0)
                | (pullMode ? PipeWireStreamFlags.Driver : 0)
                | (negotiateDevices ? PipeWireStreamFlags.Inactive : 0),
            pod[..len],
            fallback[..fallbackLen],
            capabilityPod: capability,
            cancellationToken: cancellationToken);
            _core = core;

            // Set here, not when a handler subscribes: a caller that subscribed before connecting
            // would otherwise never be hooked up, and the events it was waiting for would pass silently.
            core.OnCommand = RaiseCommand;
        }
        catch
        {
            core.Dispose();
            throw;
        }

    }

    /// <summary>
    /// This stream's own node in the graph, or <see langword="null"/> until it is connected.
    /// </summary>
    /// <remarks>
    /// A stream is a node like any other, so this is the handle for routing it:
    /// <c>graph.GetPortsForNode(await stream.WaitForNodeIdAsync())</c> finds its ports, which can then
    /// be linked. Awaited rather than read, because the id only exists once the daemon has bound the
    /// stream, which is after <c>Connect</c> returns.
    /// </remarks>
    public uint? NodeId
    {
        get
        {
            uint id = _core?.NodeId ?? NativeConstants.PW_ID_ANY;
            return id == NativeConstants.PW_ID_ANY ? null : id;
        }
    }

    /// <summary>
    /// Whether the most recent frame is kept for pulling, and in what form. Off by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retention costs something on every cycle, so it is opt-in: <see cref="FrameRetention.Owned"/>
    /// copies host bytes or duplicates dmabuf descriptors, and
    /// <see cref="FrameRetention.Borrowed"/> copies a handful of integers. Leave it
    /// <see cref="FrameRetention.None"/> for a consumer that does its work in
    /// <see cref="FrameReady"/>.
    /// </para>
    /// <para>
    /// Only the newest frame is kept. A puller slower than the producer sees the current frame and
    /// misses the ones in between, which is what a live sender wants: a late frame is worth less
    /// than a current one, and queueing them would trade latency for content nobody will show.
    /// </para>
    /// </remarks>
    public FrameRetention Retention { get; set; }

    /// <summary>
    /// Takes the most recent frame, if one has arrived since the last call.
    /// </summary>
    /// <param name="frame">
    /// The frame, which the caller owns and must dispose. Null when none is available.
    /// </param>
    /// <returns>True when a frame was taken.</returns>
    /// <remarks>
    /// <para>
    /// The pull counterpart to <see cref="FrameReady"/>, for a consumer running on its own clock
    /// rather than the graph's. Requires <see cref="Retention"/> to be <see cref="FrameRetention.Owned"/>; otherwise
    /// this always returns false, rather than silently starting to allocate.
    /// </para>
    /// <para>
    /// Taking a frame clears it, so two calls with no cycle in between return the frame once. Safe
    /// to call from any thread.
    /// </para>
    /// <para>
    /// Under explicit sync the frame also holds the producer's release point, and signals it when it
    /// is disposed: the producer does not write into that buffer again until then. Dispose it as soon
    /// as it has been read. Holding one costs the producer one buffer from its pool, not a stall.
    /// </para>
    /// </remarks>
    public bool TryGetFrame(out PulledVideoFrame? frame)
    {
        frame = Interlocked.Exchange(ref _latest, null);
        return frame is not null;
    }

    /// <summary>
    /// Takes the most recent frame by value, borrowing the pool's descriptors.
    /// </summary>
    /// <param name="frame">The frame, valid until the next pull or the next state change.</param>
    /// <returns>True when a frame was taken.</returns>
    /// <remarks>
    /// <para>
    /// The allocation-free pull, for a GPU consumer that imports the descriptor and submits within
    /// its own cadence. Requires <see cref="Retention"/> to be
    /// <see cref="FrameRetention.Borrowed"/>. Taking a frame clears it, so two calls with no cycle
    /// in between return it once. Safe to call from any thread.
    /// </para>
    /// <para>
    /// Nothing here is owned, so there is nothing to dispose and nothing to close. Without explicit
    /// sync the contents are only good until the producer recycles the buffer - see
    /// <see cref="BorrowedVideoFrame"/>. With it, the producer waits for this frame's release point
    /// before writing into its buffer again, and the release is signalled when the next frame is
    /// pulled, when <see cref="ReleaseBorrowedFrame"/> is called, or when the stream stops - so a GPU
    /// consumer may import the descriptor and read it across cycles until it lets go.
    /// </para>
    /// </remarks>
    public bool TryGetBorrowedFrame(out BorrowedVideoFrame frame)
    {
        SyncRelease previous;
        bool had;
        lock (_borrowGate)
        {
            frame = _borrowed;
            had = _hasBorrowed;
            _hasBorrowed = false;
            _borrowed = default;

            // Pulling again means the caller is done with the frame it pulled before - that is the
            // documented lifetime - so that frame's release is owed now, and this one's is held.
            previous = had ? _heldRelease : default;
            if (had)
            {
                _heldRelease = _borrowedRelease;
                _borrowedRelease = default;
            }
        }

        SignalRelease(previous);
        return had;
    }

    /// <summary>
    /// Tells the producer that the frame last taken with <see cref="TryGetBorrowedFrame"/> is no longer
    /// being read, so its buffer may be written again.
    /// </summary>
    /// <remarks>
    /// Only matters under explicit sync, where the producer waits for this before reusing the buffer.
    /// Pulling the next frame or the stream stopping does the same, so this is for a consumer that
    /// finishes early - one that has submitted its GPU work and waited for it - and wants the buffer
    /// back in rotation sooner. Calling it with nothing held does nothing.
    /// </remarks>
    public void ReleaseBorrowedFrame()
    {
        SyncRelease held;
        lock (_borrowGate)
        {
            held = _heldRelease;
            _heldRelease = default;
        }

        SignalRelease(held);
    }


    /// <summary>
    /// From inside a <c>FrameReady</c> handler: drop this frame instead of consuming it.
    /// </summary>
    /// <remarks>
    /// The buffer goes straight back to the pool without being counted as consumed. A consumer
    /// that is behind and would rather skip than fall further behind should say so here, so the
    /// queue depth other code reads stays honest about what was actually taken.
    /// </remarks>
    public void SkipCurrentFrame() => _core?.SkipCurrentBuffer();

    /// <summary>
    /// Asks the producer to renegotiate, offering a different size or frame rate.
    /// </summary>
    /// <param name="formats">Pixel formats to offer, most preferred first.</param>
    /// <param name="width">Preferred width in pixels.</param>
    /// <param name="height">Preferred height in pixels.</param>
    /// <param name="frameRate">Preferred frame rate.</param>
    /// <returns>True when the offer was sent.</returns>
    /// <remarks>
    /// For a consumer adapting to something outside the graph - a transport shedding resolution
    /// under bandwidth pressure, say. The producer answers by running the format exchange again, so
    /// the new format arrives through the normal negotiation path; a true return means the offer
    /// went out, not that it was accepted.
    /// </remarks>
    public bool RequestFormat(
        ReadOnlySpan<PixelFormat> formats, int width, int height, int frameRate = 30)
    {
        if (_core is null || formats.IsEmpty) return false;

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegative(frameRate);

        Span<byte> pod = stackalloc byte[1024];
        int len = SpaFormatPod.WriteVideoFormat(
            pod, formats, (uint)width, (uint)height, (uint)frameRate, fixedSize: false);

        return _core.RequestFormats(pod[..len]) >= 0;
    }

    /// <summary>Whether the daemon has made this stream the graph's driver.</summary>
    public bool IsDriving => _core?.IsDriving ?? false;

    /// <summary>
    /// Asks for one cycle now. Only meaningful on a stream connected with <c>pullMode</c>.
    /// </summary>
    /// <remarks>
    /// The pull primitive: a consumer that drives calls this on its own schedule, or in answer to
    /// <see cref="SpaNodeCommand.RequestProcess"/> from <see cref="CommandReceived"/>. Safe to call
    /// from any thread - the trigger does its own hop onto the data loop.
    /// </remarks>
    public void TriggerProcess() => _core?.TriggerProcess();

    /// <summary>
    /// Extra node properties to set when connecting, on top of the ones this stream sets itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Session managers, routing rules and anything showing the user a list of streams read these,
    /// so the tags are how a stream explains itself to the rest of the system. <c>pw-cat</c> sets
    /// <c>media.title</c>, <c>media.artist</c> and the rest for exactly that reason.
    /// </para>
    /// <para>
    /// Applied at connect, and a key set here wins over this stream's own default for that key -
    /// which is the point, since overriding <c>media.role</c> or <c>node.description</c> is the
    /// usual reason to reach for this. To change a tag on a stream that is already running, use
    /// <c>UpdateProperties</c> instead; some keys, <c>target.object</c> among them, are only read
    /// when the connection is made.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, string>? ExtraProperties { get; set; }

    /// <summary>How much this stream currently holds, or null when it cannot be read.</summary>
    /// <remarks>
    /// The error term for a rate controller. Pair it with <see cref="PipeWireRateController"/> to
    /// close the loop the way upstream's tunnels do.
    /// </remarks>
    public PipeWireStreamQueue? Queue => _core?.Queue;

    /// <summary>Whether the daemon has put this stream in lazy scheduling.</summary>
    public bool IsLazy => _core?.IsLazy ?? false;

    /// <summary>Adds or replaces properties on the live stream.</summary>
    /// <param name="properties">The keys to set. An empty value removes a key.</param>
    /// <returns>The number of properties actually changed.</returns>
    public int UpdateProperties(IReadOnlyDictionary<string, string> properties) =>
        _core?.UpdateProperties(properties) ?? 0;

    /// <summary>Raised on the loop thread when the daemon sends this stream's node a command.</summary>
    /// <remarks>
    /// Suspend, Flush, Drain and RequestProcess all arrive here. A state change tells you the
    /// stream stopped streaming; this tells you why.
    /// </remarks>
    public event Action<SpaNodeCommand>? CommandReceived
    {
        add => _commandReceived += value;
        remove => _commandReceived -= value;
    }

    private Action<SpaNodeCommand>? _commandReceived;

    private void RaiseCommand(SpaNodeCommand command) => _commandReceived?.Invoke(command);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_core is not null)
            await _core.DisposeAsync().ConfigureAwait(false);

        // After the loop is gone, so no cycle can install another one behind us.
        Interlocked.Exchange(ref _latest, null)?.Dispose();
        InvalidateBorrowed();
    }

    /// <summary>
    /// Takes ownership of a frame so it can outlive the cycle, and installs it as the current one.
    /// </summary>
    /// <remarks>
    /// Runs on the loop thread, which is the only place the buffer's memory and descriptors are
    /// valid, so the copy and the duplication both have to happen here rather than in
    /// <see cref="TryGetFrame"/>. The frame it displaces is disposed, because a puller slower than
    /// the producer would otherwise leak one descriptor set per cycle.
    /// </remarks>
    private void StoreLatest(in VideoFrame frame, SyncRelease release)
    {
        PulledVideoFrame pulled;
        try
        {
            pulled = Take(frame);
        }
        catch (IOException ex)
        {
            // Duplicating a descriptor failed, which on this path means the process is out of them.
            // The cycle itself is still fine, so carry on delivering to FrameReady and let the
            // puller see no new frame rather than tearing down the stream.
            LogFrameCaptureFailed(ex);
            SignalRelease(release);
            return;
        }

        // The owned frame carries its release and signals it when disposed - including when a newer
        // frame replaces it here unread.
        pulled.HoldRelease(release);
        Interlocked.Exchange(ref _latest, pulled)?.Dispose();
    }

    /// <summary>
    /// Snapshots a frame by value, keeping the pool's descriptor numbers rather than copies of them.
    /// </summary>
    /// <remarks>
    /// Runs on the loop thread. Nothing is allocated and no syscall is made: the whole point of this
    /// path is that retaining a frame costs a struct copy. The lock is what makes the hand-off safe
    /// - a struct this size is not written atomically, so a puller reading it unguarded could see
    /// half of one frame and half of the next.
    /// </remarks>
    private void StoreBorrowed(in VideoFrame frame, SyncRelease release)
    {
        Span<BorrowedVideoPlane> planes = stackalloc BorrowedVideoPlane[BorrowedPlaneArray.MaxPlanes];
        int count = 0;

        if (frame.IsFdBacked)
        {
            foreach (VideoPlane plane in frame.Planes)
            {
                if (count == planes.Length)
                    break;

                planes[count++] = new BorrowedVideoPlane(
                    plane.Fd, plane.Offset, (uint)plane.Stride, plane.Size);
            }
        }

        BorrowedVideoFrame snapshot = new(
            planes[..count],
            frame.Stride,
            frame.Width,
            frame.Height,
            frame.Format,
            frame.DrmFourcc,
            frame.Modifier,
            frame.SequenceNumber,
            frame.PresentationTimestampNs,
            frame.QueuedTimeNs,
            frame.GraphTimeNs,
            frame.StreamPositionNs,
            frame.DelayNs,
            frame.Crop,
            frame.Transform);

        // A frame replaced before anyone pulled it was never read, so its release is owed now.
        SyncRelease superseded;
        lock (_borrowGate)
        {
            superseded = _borrowedRelease;
            _borrowed = snapshot;
            _hasBorrowed = true;
            _borrowedRelease = release;
        }

        SignalRelease(superseded);
    }

    /// <summary>
    /// Drops any borrowed frame. Called when the stream leaves streaming, because a renegotiation
    /// or a disconnect tears down the buffer pool and closes the descriptors it lent out.
    /// </summary>
    private void InvalidateBorrowed()
    {
        SyncRelease waiting, held;
        lock (_borrowGate)
        {
            _borrowed = default;
            _hasBorrowed = false;
            waiting = _borrowedRelease;
            held = _heldRelease;
            _borrowedRelease = default;
            _heldRelease = default;
        }

        // The pool is going, so nothing will read these buffers again; a producer still waiting on
        // their release points must not be left waiting.
        SignalRelease(waiting);
        SignalRelease(held);
    }

    /// <summary>Copies a borrowed frame into one that owns everything it points at.</summary>
    private static PulledVideoFrame Take(in VideoFrame frame)
    {
        ImmutableArray<PulledVideoPlane> planes = ImmutableArray<PulledVideoPlane>.Empty;

        if (frame.IsFdBacked && !frame.Planes.IsEmpty)
        {
            var builder = ImmutableArray.CreateBuilder<PulledVideoPlane>(frame.Planes.Length);
            try
            {
                foreach (VideoPlane plane in frame.Planes)
                {
                    builder.Add(new PulledVideoPlane(
                        plane.DuplicateFd(), plane.Offset, plane.Stride, plane.Size));
                }
            }
            catch
            {
                // Partway through, so the descriptors already duplicated are ours and nobody else
                // will ever see them. Close them before letting the failure out.
                foreach (PulledVideoPlane done in builder)
                    done.Descriptor.Dispose();
                throw;
            }

            planes = builder.MoveToImmutable();
        }

        return new PulledVideoFrame(
            pixels: planes.IsEmpty ? [.. frame.Pixels] : ImmutableArray<byte>.Empty,
            planes: planes,
            stride: frame.Stride,
            width: frame.Width,
            height: frame.Height,
            format: frame.Format,
            drmFourcc: frame.DrmFourcc,
            modifier: frame.Modifier,
            sequenceNumber: frame.SequenceNumber,
            bufferType: frame.BufferType,
            color: frame.Color,
            presentationTimestampNs: frame.PresentationTimestampNs,
            queuedTimeNs: frame.QueuedTimeNs,
            graphTimeNs: frame.GraphTimeNs,
            streamPositionNs: frame.StreamPositionNs,
            delayNs: frame.DelayNs,
            crop: frame.Crop,
            transform: frame.Transform);
    }

    private unsafe void OnBuffer(spa_data* d, pw_buffer* buf, in PipeWireStreamCore.StreamClock clock)
    {
        if (d->chunk is null) return;

        uint offset = d->chunk->offset;
        uint size   = d->chunk->size;
        if (size == 0) return;

        // The chunk header lives in memory the producer owns, so its offset and size are inputs,
        // not facts. A span built from an out-of-range pair reads straight past the mapping, and a
        // size above int.MaxValue casts to a negative length.
        if ((ulong)offset + size > d->maxsize) return;
        if (size > int.MaxValue) return;

        // data may be null for a pure DMA-BUF buffer that wasn't host-mapped.
        var pixels = d->data is null
            ? ReadOnlySpan<byte>.Empty
            : new ReadOnlySpan<byte>((byte*)d->data + offset, (int)size);

        PipeWireBufferType bufferType = SpaFormatPod.ToBufferType((SpaDataType)d->type);
        bool fdBacked = bufferType is PipeWireBufferType.DmaBuf or PipeWireBufferType.MemFd;
        long fd = fdBacked && (long)d->fd >= 0 ? (long)d->fd : -1;

        // Surface every plane of an fd-backed frame so a consumer can import it zero-copy: a planar
        // dmabuf (NV12) carries one spa_data per plane, each with its own fd/offset/stride. Host-memory
        // frames keep the single-plane view (Planes stays empty).
        spa_buffer* spaBuf = buf->buffer;
        Span<VideoPlane> planes = stackalloc VideoPlane[8];
        int planeCount = 0;
        if (fdBacked)
        {
            uint n = Math.Min(spaBuf->n_datas, (uint)planes.Length);
            for (uint i = 0; i < n; i++)
            {
                spa_data* p = &spaBuf->datas[i];
                if (p->chunk is null) continue;

                // A plane the producer did not back has fd -1, and handing that to an importer
                // fails as EINVAL somewhere deep in the driver rather than here where it can be named.
                if ((long)p->fd < 0) continue;
                // For dmabuf the plane's offset within its fd is chunk->offset; mapoffset is an mmap
                // concept (0 for dmabuf). stride is signed in SPA but always >= 0 for video here.
                planes[planeCount++] = new VideoPlane(
                    (long)p->fd, p->chunk->offset, p->chunk->stride, p->maxsize);
            }
        }

        SpaFormatPod.VideoFormatInfo fmt = Format;

        // Nothing is negotiated, so nothing describes this buffer. Emitting it anyway hands the
        // handler a frame whose geometry is zero or, worse, the previous negotiation's.
        if (fmt.Width <= 0 || fmt.Height <= 0) return;

        // Read on the loop thread while the buffer is still ours; the spans die with it.
        Span<VideoRegion> damage = stackalloc VideoRegion[DamageRegions];
        int damageCount = SpaFormatPod.ReadDamage(spaBuf, damage);
        bool hasCursor = SpaFormatPod.TryFindCursor(spaBuf, out VideoCursor cursor);

        var frame = new VideoFrame(
            pixels, d->chunk->stride, fmt.Width, fmt.Height, fmt.Format, ++_sequence,
            bufferType: bufferType,
            fd: fd,
            mapOffset: d->mapoffset,
            presentationTimestampNs: SpaFormatPod.FindPresentationTimestampNs(buf),
            queuedTimeNs: SpaFormatPod.QueuedTimeNs(buf),
            color: fmt.Color,
            graphTimeNs: clock.GraphTimeNs,
            streamPositionNs: clock.StreamPositionNs,
            delayNs: clock.DelayNs,
            modifier: fdBacked ? fmt.Modifier : DrmFormatModifier.Invalid,
            planes: planes[..planeCount],
            syncTimeline: SpaFormatPod.TryFindSyncTimeline(spaBuf, out SpaFormatPod.SyncTimeline t)
                ? new VideoSyncTimeline(t.Flags, t.AcquirePoint, t.ReleasePoint)
                : null,
            crop: SpaFormatPod.FindCrop(spaBuf),
            transform: SpaFormatPod.FindTransform(spaBuf),
            damage: damage[..damageCount],
            cursor: hasCursor ? cursor : default,
            hasCursor: hasCursor);

        // Explicit sync, when the producer negotiated it: wait for the acquire point before anything
        // reads a byte, and promise the release point. Present-driven, not flag-driven: no timeline
        // meta means implicit fences still apply and nothing here runs.
        //
        // The promise is made now, by clearing UNSCHEDULED_RELEASE before the buffer goes back, but
        // the signal is sent when the reader has finished - at once for a frame only handed to
        // FrameReady, on dispose for an owned frame, and on the next pull for a borrowed one. That
        // is upstream video-play-sync's order: it signals after it has rendered, not when the
        // buffer arrived.
        SyncRelease release = default;
        if (frame.SyncTimeline is { } timeline
            && SpaFormatPod.TryFindSyncDataFds(spaBuf, out int acquireFd, out int releaseFd)
            && acquireFd >= 0 && releaseFd >= 0)
        {
            if (timeline.AcquirePoint != 0 && WaitAcquire(acquireFd, timeline.AcquirePoint) is { Reached: false } missed)
            {
                // Never ready. Handing it on would mean reading pixels still being written; no
                // promise has been made yet, so the producer is not left waiting on this buffer.
                if (missed.Outcome == SyncWaitOutcome.TimedOut)
                    LogAcquireTimedOut(timeline.AcquirePoint, AcquireTimeout.TotalMilliseconds);
                else
                    LogAcquireFailed(timeline.AcquirePoint, missed.Errno);
                return;
            }

            release = SyncRelease.For(releaseFd, timeline.ReleasePoint);
            if (release.IsPending) ClearSyncUnscheduled(spaBuf);
        }

        switch (Retention)
        {
            case FrameRetention.Owned:
                StoreLatest(frame, release);
                release = default;
                break;
            case FrameRetention.Borrowed:
                StoreBorrowed(frame, release);
                release = default;
                break;
            default:
                break;
        }

        FrameReady?.Invoke(this, frame);

        // Not retained, so the handler was the last reader.
        SignalRelease(release);
    }

    /// <summary>Waits for a frame's acquire point, on a syncobj or on upstream's eventfd stand-in.</summary>
    /// <remarks>
    /// A descriptor that is neither fails the wait rather than being read as an eventfd; see
    /// <see cref="SyncTimeline"/> for how that used to pass frames through unsynchronised.
    /// </remarks>
    private static SyncWait WaitAcquire(int acquireFd, ulong point) =>
        SyncTimeline.Wait(acquireFd, point, AcquireTimeout);

    /// <summary>Clears the unscheduled-release flag in a buffer's timeline meta, if present.</summary>
    private static unsafe void ClearSyncUnscheduled(spa_buffer* sb)
    {
        if (sb is null || sb->metas is null) return;

        uint count = Math.Min(sb->n_metas, 64u);
        for (uint i = 0; i < count; i++)
        {
            spa_meta* m = &sb->metas[i];
            if (m->type != (uint)SpaMetaType.SyncTimeline || m->data is null) continue;
            if (m->size < (uint)sizeof(spa_meta_sync_timeline)) continue;
            ((spa_meta_sync_timeline*)m->data)->flags &= ~(uint)SpaMetaSyncTimelineFlags.UnscheduledRelease;
        }
    }

    private void OnState(PipeWireStreamState oldState, PipeWireStreamState newState)
    {
        // Leaving Streaming means the buffer pool is going, and with it the descriptors a borrowed
        // frame is pointing at. Drop it before anyone can pull one that names a closed descriptor -
        // which would not fail, because the number gets reused.
        if (oldState == PipeWireStreamState.Streaming && newState != PipeWireStreamState.Streaming)
            InvalidateBorrowed();

        StateChanged?.Invoke(this, oldState, newState);
    }

    // video-play-fixate's on_stream_peer_capability_changed: announce the real formats once the
    // producer's capabilities are known, then activate. Once: a later PeerCapability (a relink) would
    // otherwise restart negotiation under a running stream. Loop lock held.
    private unsafe void OnPeerConnected(PipeWireStreamCore core, spa_pod* param)
    {
        if (_announcedToAPeer) return;
        _announcedToAPeer = true;

        PeerCapabilities peer = DeviceIdNegotiation.Parse(param);
        (uint width, uint height, uint frameRate) = _preferredGeometry;
        byte[] pods = DeviceIdNegotiation.WriteDeviceFormats(peer, _deviceOffers,
            _modifierFormat, width, height, frameRate, fixedSize: false,
            hostMemoryFallback: true, out int count, out int deviceFormats);
        LogDeviceOffers(peer.NegotiatesDeviceIds, deviceFormats, _deviceOffers.Length, peer.AvailableDevices.Length);

        int rc = core.RequestParamsFromCallback(pods, count);
        if (rc < 0) LogAnnounceRefused(rc);
        core.SetActiveFromCallback(true);
    }

    private unsafe void OnFormat(spa_pod* param)
    {
        if (param is null)
        {
            // Withdrawn: the stream is unconfigured until a new format arrives, and OnBuffer stops
            // emitting because the geometry is gone.
            Volatile.Write(ref _fmtCell,
                new NegotiatedFormat(
                    new SpaFormatPod.VideoFormatInfo(PixelFormat.Unknown, 0, 0, VideoColorInfo.Unknown)));
            _modifierFixated = false;
            LogFormatWithdrawn();
            return;
        }

        SpaFormatPod.VideoFormatInfo parsed = SpaFormatPod.ParseVideoFormat(param, Format);
        Volatile.Write(ref _fmtCell, new NegotiatedFormat(parsed));
        LogNegotiatedFormat(parsed.Format, parsed.Width, parsed.Height, parsed.Modifier, parsed.ModifierNeedsFixation);
    }

    // After the format is negotiated we know the geometry, so declare our buffer needs:
    // accept host memory AND DMA-BUF (zero-copy GPU), plus request the PTS header meta.
    private void OnPostFormat(PipeWireStreamCore core)
    {
        SpaFormatPod.VideoFormatInfo fmt = Format;
        if (fmt.Format == PixelFormat.Unknown || fmt.Width <= 0 || fmt.Height <= 0) return;

        // Two-step modifier fixation: when we offered a modifier choice with DONT_FIXATE the producer
        // returns the subset it supports without collapsing it (ModifierNeedsFixation). Because we only
        // ever offer modifiers our GPU can import, the producer's preferred returned modifier
        // is always safe - so re-offer just that single value, with DONT_FIXATE cleared, to fixate
        // the negotiation. One scalar, one stack buffer, no allocation.
        if (!_modifierFixated && _modifiersOffered && fmt.ModifierNeedsFixation)
        {
            Span<byte> fixate = stackalloc byte[512];
            ReadOnlySpan<PixelFormat> chosenFormat = [_modifierFormat];
            ReadOnlySpan<long> chosen = [(long)fmt.Modifier];
            // With the negotiated device: the producer's offers each name one, mandatory.
            int fl = SpaFormatPod.WriteVideoFormat(fixate, chosenFormat,
                (uint)fmt.Width, (uint)fmt.Height, 30, fixedSize: false,
                modifiers: chosen, fixateModifier: true, deviceId: fmt.DeviceId);

            // Marked done only if the daemon took it. A refused fixation has to be retried, or the
            // negotiation stays unfixated, no buffers are ever allocated, and the stream delivers
            // nothing.
            int rc = core.RequestParamsFromCallback(fixate[..fl]);
            if (rc >= 0)
            {
                _modifierFixated = true;
                return; // a fresh param_changed will arrive with the fixated format
            }

            LogFixationRefused(rc);
        }

        // One buffer with the pods laid end to end, because seven params is past what the
        // fixed-arity overload takes. Each pod starts 8-byte aligned, as SPA requires, and carries
        // its own size so the other side can walk them.
        Span<byte> pods = stackalloc byte[8192];
        int used = 0, podCount = 0;

        static int Align(int n) => (n + 7) & ~7;

        used += Align(SpaFormatPod.WriteHeaderMetaParam(pods[used..])); podCount++;

        // Each ParamMeta names one meta type, so asking for several is several objects.
        if (_explicitSyncRequested)
        {
            used += Align(SpaFormatPod.WriteSyncTimelineMetaParam(pods[used..]));
            podCount++;
        }

        // Crop, damage, orientation and the pointer. All advisory - a producer may attach none of
        // them - but a consumer that never asks is guaranteed to get none, and then has to treat
        // every frame as fully changed, upright, and with the cursor already painted in.
        used += Align(SpaFormatPod.WriteCropMetaParam(pods[used..])); podCount++;
        used += Align(SpaFormatPod.WriteDamageMetaParam(pods[used..], DamageRegions)); podCount++;
        used += Align(SpaFormatPod.WriteTransformMetaParam(pods[used..])); podCount++;
        used += Align(SpaFormatPod.WriteCursorMetaParam(pods[used..])); podCount++;

        int stride = SpaFormatPod.VideoStride(fmt.Format, fmt.Width);
        int size = SpaFormatPod.VideoImageSize(fmt.Format, fmt.Width, fmt.Height);
        if (size <= 0)
        {
            // Geometry not known yet.
            core.RequestParamsFromCallback(pods[..used], podCount);
            return;
        }

        // Block count = number of planes. A planar format (I420=3, NV12=2) is carried as one spa_data
        // block per plane for BOTH host memory (one MemFd per plane) and DMA-BUF (one fd per plane) -
        // gst's pipewiresink splits the planes either way. Declaring a single block for a multi-plane
        // format makes the daemon reject buffer allocation ("alloc buffers: Invalid argument"); packed
        // formats are a single block. Offer host memory and DMA-BUF so a GPU producer can go zero-copy.
        // With explicit sync requested, two more blocks ride along for the acquire and release
        // timeline descriptors, mirroring the producer: a pool shaped any other way cannot carry
        // the points this consumer waits on and signals.
        int blocks = SpaFormatPod.VideoPlaneCount(fmt.Format);

        // As a consumer we do not dictate the block size: SPA_PARAM_BUFFERS_size is per block, and
        // the producer owns how it lays its planes out. Pinning a fixed figure risks refusal when
        // the producer layout differs from this arithmetic.
        int blockSize = SpaFormatPod.VideoBlockSize(fmt.Format, fmt.Width, fmt.Height);
        Span<byte> buffers = stackalloc byte[256];
        int bl = SpaFormatPod.WriteVideoBuffersParam(
            buffers, blockSize, stride, SpaFormatPod.VideoCaptureDataTypeMask, blocks,
            sizeIsAnyOf: true,
            syncDataBlocks: _explicitSyncRequested ? SpaFormatPod.SyncTimelineDataBlocks : 0);

        LogRequestedBuffers(blocks, blockSize, stride, SpaFormatPod.VideoCaptureDataTypeMask);

        // The buffers param goes first, then everything staged above.
        Span<byte> all = stackalloc byte[8192];
        buffers[..bl].CopyTo(all);
        int allUsed = Align(bl);
        pods[..used].CopyTo(all[allUsed..]);
        core.RequestParamsFromCallback(all[..(allUsed + used)], podCount + 1);
    }

    /// <summary>Signals a release this capture held, and reports a refused signal.</summary>
    private void SignalRelease(SyncRelease release)
    {
        if (!release.Signal()) LogReleaseSignalFailed();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "signalling a frame's release point was refused; the producer waits until its release timeout for that buffer")]
    private partial void LogReleaseSignalFailed();

    [LoggerMessage(Level = LogLevel.Debug, Message = "negotiated format {Format} {Width}x{Height} modifier=0x{Modifier:x} needsFixation={NeedsFixation}")]
    private partial void LogNegotiatedFormat(PixelFormat format, int width, int height, ulong modifier, bool needsFixation);

    [LoggerMessage(Level = LogLevel.Debug, Message = "requesting buffers blocks={Blocks} size={Size} stride={Stride} dataTypeMask=0x{DataTypeMask:x}")]
    private partial void LogRequestedBuffers(int blocks, int size, int stride, int dataTypeMask);

    [LoggerMessage(Level = LogLevel.Warning, Message = "a frame's acquire point {Point} was not reached within {TimeoutMs}ms; the frame was dropped rather than read while still being written")]
    private partial void LogAcquireTimedOut(ulong point, double timeoutMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "waiting for a frame's acquire point {Point} failed with errno {Errno}; the frame was dropped")]
    private partial void LogAcquireFailed(ulong point, int errno);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "peer capabilities: negotiates device ids={Negotiates}, names {PeerDevices} devices; announcing {DeviceFormats} of {Offers} device formats")]
    private partial void LogDeviceOffers(bool negotiates, int deviceFormats, int offers, int peerDevices);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "the daemon refused the announced formats ({Result}); the stream is activated anyway and will not negotiate")]
    private partial void LogAnnounceRefused(int result);

    [LoggerMessage(Level = LogLevel.Debug, Message = "the daemon withdrew the format; the stream is unconfigured")]
    private partial void LogFormatWithdrawn();

    [LoggerMessage(Level = LogLevel.Warning, Message = "the daemon refused the modifier fixation ({Result}); it will be retried on the next negotiation")]
    private partial void LogFixationRefused(int result);

    [LoggerMessage(Level = LogLevel.Warning, Message = "could not take a copy of the frame for TryGetFrame; the puller will not see this one")]
    private partial void LogFrameCaptureFailed(Exception exception);

    /// <summary>Waits until the stream is negotiated and running.</summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// <c>Connect</c> issues a request; the daemon then negotiates a format over several round
    /// trips, and only then does the stream start. Without this a caller has to subscribe to
    /// <c>StateChanged</c> and drive its own completion, which is the same code every time.
    /// <para>
    /// Cancelling abandons the wait, not the stream: the connection stays up and keeps negotiating,
    /// because there is nothing to recall. Dispose it to stop it.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Not connected yet.</exception>
    /// <exception cref="PipeWireException">The stream reached its error state instead.</exception>
    public Task WaitForStreamingAsync(CancellationToken cancellationToken = default)
    {
        PipeWireStreamCore core = _core
            ?? throw new InvalidOperationException("Connect before waiting for the stream to start.");

        return core.WaitForStreamingAsync(cancellationToken);
    }

    /// <summary>
    /// Waits until the daemon has given this stream a node id, and returns it.
    /// </summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <returns>The node id, the same value <see cref="NodeId"/> reports from then on.</returns>
    /// <remarks>
    /// <see cref="NodeId"/> is null straight after <c>Connect</c>: the stream is a proxy until the
    /// daemon binds it, and the id is only assigned then. Linking or targeting this stream needs the
    /// id, so await this rather than reading the property and hoping. It completes when the stream
    /// first reaches Paused, which is where upstream's own examples read the id.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Not connected yet.</exception>
    /// <exception cref="PipeWireException">The stream reached its error state instead.</exception>
    public Task<uint> WaitForNodeIdAsync(CancellationToken cancellationToken = default)
    {
        PipeWireStreamCore core = _core
            ?? throw new InvalidOperationException("Connect before waiting for the node id.");

        return core.WaitForNodeIdAsync(cancellationToken);
    }

    /// <summary>Every control this stream exposes, as the daemon last reported them.</summary>
    /// <remarks>
    /// Empty until the stream is connected and the daemon has reported them, which happens during
    /// negotiation. A snapshot: the daemon re-reports a control whenever one of its values changes.
    /// </remarks>
    public ImmutableArray<PipeWireStreamControl> Controls =>
        _core?.Controls ?? [];

    /// <summary>One control by SPA property id, or null when the stream has not reported it.</summary>
    public PipeWireStreamControl? GetControl(uint id) => _core?.GetControl(id);

    /// <summary>Sets a control's values.</summary>
    /// <param name="id">The SPA property id, as carried by <see cref="PipeWireStreamControl.Id"/>.</param>
    /// <param name="values">
    /// One value for a scalar control, or one per channel. More than
    /// <see cref="PipeWireStreamControl.MaximumValues"/> is the daemon's to refuse, not this
    /// library's to guess at.
    /// </param>
    /// <param name="cancellationToken">Abandons the wait for the loop lock.</param>
    /// <remarks>
    /// Sent as a <c>Props</c> object. The daemon applies it when it next runs the node, so this
    /// returning does not mean the value is in effect; read it back from <see cref="Controls"/> if
    /// that matters.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Not connected yet.</exception>
    /// <exception cref="ArgumentException"><paramref name="values"/> is empty.</exception>
    public void SetControl(uint id, ReadOnlySpan<float> values, CancellationToken cancellationToken = default)
    {
        if (values.IsEmpty)
            throw new ArgumentException("a control needs at least one value.", nameof(values));

        PipeWireStreamCore core = _core
            ?? throw new InvalidOperationException("Connect before setting a control.");

        core.SetControl(id, values, cancellationToken);
    }

    /// <summary>The graph clock as of the last cycle, or null before the graph offers it.</summary>
    /// <remarks>
    /// Two streams on one context are driven by the same clock, so their clocks are directly
    /// comparable - which is what lets audio and video be lined up against each other.
    /// </remarks>
    public PipeWireGraphClock? GraphClock => _core?.GraphClock;

    /// <summary>What the graph's resampler is doing for this stream, or null if none is.</summary>
    public PipeWireRateMatch? RateMatch => _core?.RateMatch;

    /// <summary>
    /// Applies a rate correction to this stream, 1.0 being none.
    /// </summary>
    /// <remarks>
    /// For bridging the graph clock to one this library does not own - a network transport, another
    /// device. The upstream pattern is to derive the correction from how far the queue is from its
    /// target, smooth it, and apply it here; see PipeWire's own rtp and tunnel modules. Applying an
    /// unsmoothed correction makes the drift worse rather than better.
    /// </remarks>
    public void SetRate(double rate) => _core?.SetRate(rate);

    /// <summary>
    /// Announces the latency this stream adds, so the rest of the graph can compensate.
    /// </summary>
    /// <remarks>
    /// Anything holding a queue - a network transport, an encoder - adds delay that nothing else
    /// can see. Left unannounced it becomes drift between this stream and everything it is meant
    /// to stay in sync with. Pass the process latency too when the delay is per-cycle rather than
    /// fixed; PipeWire's own transport modules announce both.
    /// </remarks>
    public void AnnounceLatency(PipeWireLatency latency, PipeWireProcessLatency? processLatency = null)
    {
        ArgumentNullException.ThrowIfNull(latency);
        _core?.AnnounceLatency(latency, processLatency);
    }
}
