using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Media;

/// <summary>
/// Centralizes SPA format-pod construction and the managed-to-SPA format-enum mappings
/// shared by the video/audio capture and output stream wrappers.
/// </summary>
internal static class SpaFormatPod
{
    // - Param: request the SPA_META_Header so buffers carry PTS -

    /// <summary>
    /// Writes a ParamMeta object requesting <c>SPA_META_Header</c> of the given size, so the
    /// daemon allocates buffers with a header and the producer fills in the presentation
    /// timestamp. Without this, frames never carry a PTS.
    /// </summary>
    internal static unsafe int WriteHeaderMetaParam(Span<byte> buf)
    {
        var b = new SpaPodBuilder(buf);
        b.PushObject(SpaType.ObjectParamMeta, SpaParamType.Meta);
        b.AddId(SpaParamMeta.Type, SpaMetaType.Header);
        b.AddInt(SpaParamMeta.Size, sizeof(spa_meta_header));
        return b.GetPod().Length;
    }

    /// <summary>
    /// Writes a ParamMeta object requesting <c>SPA_META_SyncTimeline</c>, so a DMA-BUF producer can
    /// hand over explicit acquire and release points instead of relying on implicit fences.
    /// </summary>
    /// <remarks>
    /// Opt-in and separate from the header request, because asking for it changes the buffer layout:
    /// a buffer carrying this meta comes with two extra descriptors, one per timeline
    /// (<c>spa/buffer/meta.h:190-192</c>). A consumer that asks for it and then ignores the points
    /// is worse off than one that never asked, because the producer stops adding implicit fences.
    /// </remarks>
    internal static unsafe int WriteSyncTimelineMetaParam(Span<byte> buf)
    {
        var b = new SpaPodBuilder(buf);
        b.PushObject(SpaType.ObjectParamMeta, SpaParamType.Meta);
        b.AddId(SpaParamMeta.Type, SpaMetaType.SyncTimeline);
        b.AddInt(SpaParamMeta.Size, sizeof(spa_meta_sync_timeline));
        return b.GetPod().Length;
    }

    /// <summary>Reads a buffer's sync timeline, if it carries one.</summary>
    /// <remarks>
    /// Only present when the meta was requested and the producer agreed. The points are timeline
    /// values, not descriptors: the descriptors they refer to are the two extra entries in the
    /// buffer's data array, and they belong to the pool for the handler's duration like every other
    /// borrowed descriptor.
    /// </remarks>
    internal static unsafe bool TryFindSyncTimeline(spa_buffer* buf, out SyncTimeline timeline)
    {
        timeline = default;
        if (buf is null || buf->metas is null) return false;

        uint wanted = (uint)SpaMetaType.SyncTimeline;
        uint count = Math.Min(buf->n_metas, MaxMetasWalked);

        for (uint i = 0; i < count; i++)
        {
            spa_meta* m = &buf->metas[i];
            if (m->type != wanted || m->data is null) continue;
            if (m->size < (uint)sizeof(spa_meta_sync_timeline)) continue;

            spa_meta_sync_timeline* t = (spa_meta_sync_timeline*)m->data;
            timeline = new SyncTimeline(t->flags, t->acquire_point, t->release_point);
            return true;
        }

        return false;
    }

    /// <summary>A buffer's explicit synchronisation points.</summary>
    /// <param name="Flags">Producer flags, as reported.</param>
    /// <param name="AcquirePoint">The timeline point at which the data may be read.</param>
    /// <param name="ReleasePoint">
    /// The timeline point the consumer signals when it is finished with the data.
    /// </param>
    internal readonly record struct SyncTimeline(uint Flags, ulong AcquirePoint, ulong ReleasePoint);

    /// <summary>Finds the acquire and release timeline descriptors of a buffer, in order.</summary>
    /// <remarks>
    /// Located by data type, not by position: the sync blocks ride past however many plane blocks
    /// the format negotiated, and only their <c>SyncObj</c> type marks them. Both must be present
    /// and usable, or the buffer cannot take part in explicit sync.
    /// </remarks>
    internal static unsafe bool TryFindSyncDataFds(spa_buffer* buf, out int acquireFd, out int releaseFd)
    {
        acquireFd = -1;
        releaseFd = -1;
        if (buf is null || buf->datas is null) return false;

        // Bounded for the same reason every other pool walk is: the count belongs to the pool.
        uint count = Math.Min(buf->n_datas, 128u);
        uint found = 0;
        for (uint i = 0; i < count; i++)
        {
            if (buf->datas[i].type != (uint)SpaDataType.SyncObj) continue;
            long fd = (long)buf->datas[i].fd;
            if (fd < 0 || fd > int.MaxValue) return false;
            if (found == 0) acquireFd = (int)fd; else releaseFd = (int)fd;
            if (++found == 2) return true;
        }

        acquireFd = -1;
        releaseFd = -1;
        return false;
    }

    // - Param: buffer requirements (declares accepted data types incl. DMA-BUF) -

    /// <summary>Extra data blocks for explicit-sync timeline descriptors: acquire, then release.</summary>
    internal const int SyncTimelineDataBlocks = 2;

    /// <summary>
    /// Writes a ParamBuffers object advertising the accepted buffer data types and count.
    /// Including <c>SPA_DATA_DmaBuf</c> in <paramref name="dataTypes"/> opts into zero-copy GPU
    /// buffers; the producer picks one of the offered types, falling back to host memory when it
    /// cannot provide DMA-BUF.
    /// </summary>
    /// <param name="buf">Destination for the pod.</param>
    /// <param name="size">
    /// Size of <em>one data block</em>, not of the whole image. SPA documents
    /// <c>SPA_PARAM_BUFFERS_size</c> as "size of a data block memory", so a planar format declaring
    /// three blocks describes its largest plane here, not the sum of them - see
    /// <see cref="VideoBlockSize"/>.
    /// </param>
    /// <param name="stride">Stride of a data block.</param>
    /// <param name="dataTypes">Mask of acceptable <c>SpaDataType</c> values.</param>
    /// <param name="blocks">Data blocks per buffer: one per plane.</param>
    /// <param name="sizeIsAnyOf">
    /// When true the size is offered as an open range rather than a fixed value, which is what a
    /// consumer wants: the producer owns the layout and the consumer reads whatever <c>chunk-&gt;size</c> says.
    /// A fixed value risks refusal when the producer lays its planes out differently. PipeWire's own
    /// <c>gstpipewiresrc</c> offers a range for the same reason.
    /// </param>
    /// <param name="syncDataBlocks">
    /// Extra data blocks for explicit-sync timeline descriptors, appended after the plane blocks.
    /// Two for the acquire and release timelines (<c>spa/buffer/meta.h:186-192</c>), which also
    /// requires the <c>SyncTimeline</c> metaType below: without it the pool has nowhere to carry
    /// the points the descriptors order.
    /// </param>
    internal static int WriteVideoBuffersParam(
        Span<byte> buf, int size, int stride, int dataTypes, int blocks = 1, bool sizeIsAnyOf = false,
        int syncDataBlocks = 0)
    {
        var b = new SpaPodBuilder(buf);
        b.PushObject(SpaType.ObjectParamBuffers, SpaParamType.Buffers);
        b.AddChoiceRangeInt(SpaParamBuffers.Buffers, 8, 2, 16);
        // Blocks = number of planes (one spa_data block per plane). A packed format / host buffer is
        // a single block; planar needs one block per plane so each plane gets its own fd.
        b.AddInt(SpaParamBuffers.Blocks, blocks + syncDataBlocks);

        if (sizeIsAnyOf)
            b.AddChoiceRangeInt(SpaParamBuffers.Size, size, 1, int.MaxValue);
        else
            b.AddInt(SpaParamBuffers.Size, size);

        b.AddInt(SpaParamBuffers.Stride, stride);
        b.AddChoiceFlagsInt(SpaParamBuffers.DataType, dataTypes);

        // Mandatory, not advisory: a peer that cannot carry timeline metadata must refuse rather
        // than silently accept buffers whose ordering guarantees it cannot keep, which would read
        // as GPU corruption rather than a failed negotiation.
        if (syncDataBlocks > 0)
            b.AddInt(SpaParamBuffers.MetaType, 1 << (int)SpaMetaType.SyncTimeline,
                SpaPodPropFlag.Mandatory);

        return b.GetPod().Length;
    }

    /// <summary>
    /// Size of the largest single data block for a format, which is what
    /// <c>SPA_PARAM_BUFFERS_size</c> describes when more than one block is declared.
    /// </summary>
    /// <remarks>
    /// The luma plane is the largest in every planar layout we support, so the block size is one
    /// full-height plane at the primary stride. Passing the whole-image size instead over-declares
    /// each block by roughly the chroma, which the daemon rejects.
    /// </remarks>
    internal static int VideoBlockSize(PixelFormat fmt, int width, int height) =>
        VideoPlaneCount(fmt) == 1
            ? VideoImageSize(fmt, width, height)
            : VideoStride(fmt, width) * height;

    /// <summary>Primary-plane stride (bytes per row) for the negotiated format.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The width is negative, or the row does not fit in <see cref="int"/> bytes.
    /// </exception>
    internal static int VideoStride(PixelFormat fmt, int width)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);

        long stride = (long)width * BytesPerPixel(fmt);
        if (stride > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width),
                $"a {width}px {fmt} row needs {stride} bytes, which does not fit in an int.");

        return (int)stride;
    }

    /// <summary>Total buffer size in bytes for the negotiated format (incl. planar chroma planes).</summary>
    /// <remarks>
    /// <para>
    /// Chroma planes are <c>ceil(w/2) x ceil(h/2)</c>, not <c>w*h/4</c>. The difference only shows on
    /// odd dimensions, where the rounded-down form under-allocates and the producer writes past the
    /// buffer: 5x7 I420 needs 59 bytes and <c>w*h*3/2</c> yields 52.
    /// </para>
    /// <para>
    /// Width and height arrive from a negotiated param, so they are not trusted. The arithmetic runs
    /// in 64-bit and a frame that cannot be addressed is rejected rather than wrapped - at 32768
    /// square, packed 32bpp is exactly 2^32 bytes and truncates to a zero-sized buffer.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A dimension is negative, or the frame does not fit in <see cref="int"/> bytes.
    /// </exception>
    internal static int VideoImageSize(PixelFormat fmt, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);

        long w = width, h = height;
        long chroma = ((w + 1) / 2) * ((h + 1) / 2);

        long size = fmt switch
        {
            PixelFormat.Yuv420 => (w * h) + (2 * chroma),   // Y + U + V
            PixelFormat.Nv12   => (w * h) + (2 * chroma),   // Y + interleaved UV
            PixelFormat.Yuyv   => w * h * 2,

            // Packed formats only. Anything else has no layout this code knows, and assuming four
            // bytes per pixel for it sizes the buffer from a guess.
            PixelFormat.Rgba or PixelFormat.Bgra or PixelFormat.Rgbx or PixelFormat.Bgrx
                => w * h * 4,
            _ => throw new ArgumentOutOfRangeException(
                nameof(fmt), fmt, "no known image size for this format"),
        };

        if (size > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width),
                $"a {width}x{height} {fmt} frame needs {size} bytes, which does not fit in an int.");

        return (int)size;
    }

    /// <summary>Accepted capture data types: host memory + DMA-BUF (zero-copy GPU).</summary>
    internal static int VideoCaptureDataTypeMask =>
        (1 << (int)SpaDataType.MemPtr) | (1 << (int)SpaDataType.MemFd) | (1 << (int)SpaDataType.DmaBuf);

    /// <summary>
    /// Number of memory blocks a buffer carries for the format: one block per plane. gst's pipewiresink
    /// (and PipeWire's convention) splits a planar format into one <c>spa_data</c> per plane for both
    /// host memory and DMA-BUF, so a consumer must declare a block per plane (I420=3, NV12=2). Packed
    /// formats are a single block.
    /// </summary>
    internal static int VideoPlaneCount(PixelFormat fmt) => fmt switch
    {
        PixelFormat.Yuv420 => 3,   // Y + U + V
        PixelFormat.Nv12   => 2,   // Y + interleaved UV
        _                  => 1,   // packed
    };

    // - Buffer metadata -

    /// <summary>Maps a raw <c>spa_data.type</c> to <see cref="PipeWireBufferType"/>.</summary>
    internal static PipeWireBufferType ToBufferType(SpaDataType spaDataType) => spaDataType switch
    {
        SpaDataType.MemPtr => PipeWireBufferType.MemPtr,
        SpaDataType.MemFd  => PipeWireBufferType.MemFd,
        SpaDataType.DmaBuf => PipeWireBufferType.DmaBuf,
        _                  => PipeWireBufferType.Unknown,
    };

    /// <summary>
    /// Finds the presentation timestamp (ns) from a buffer's SPA_META_Header, or -1 if absent.
    /// </summary>
    /// <remarks>
    /// Video producers populate this; PipeWire audio typically does NOT carry a per-buffer
    /// header PTS (audio timing is derived from the graph clock + sample position), so audio
    /// frames usually report -1 here.
    /// </remarks>
    /// <summary>The most metadata entries a buffer will be walked for.</summary>
    /// <remarks>
    /// A bound on someone else's count. The buffer struct belongs to the pool, so <c>n_metas</c> is
    /// a number this process reads rather than one it set, and walking it unchecked turns a wrong
    /// value into an out-of-bounds read on the realtime path. Real buffers carry a handful: header,
    /// cursor, region, and a few more.
    /// </remarks>
    private const uint MaxMetasWalked = 64;

    internal static unsafe long FindPresentationTimeNs(spa_buffer* buf)
    {
        if (buf is null || buf->metas is null) return -1;
        uint headerType = (uint)SpaMetaType.Header;
        uint count = Math.Min(buf->n_metas, MaxMetasWalked);
        for (uint i = 0; i < count; i++)
        {
            spa_meta* m = &buf->metas[i];
            if (m->type == headerType && m->data is not null && m->size >= (uint)sizeof(spa_meta_header))
                return (long)((spa_meta_header*)m->data)->pts;
        }
        return -1;
    }

    // - Format-pod parsing (param_changed) -

    /// <summary>The negotiated video format extracted from a Format pod.</summary>
    /// <param name="Format">Negotiated pixel format.</param>
    /// <param name="Width">Frame width in pixels.</param>
    /// <param name="Height">Frame height in pixels.</param>
    /// <param name="Color">Negotiated color metadata.</param>
    /// <param name="Modifier">
    /// The negotiated DRM format modifier, or <see cref="DrmFormatModifier.Invalid"/> when none was
    /// negotiated (host-memory path). When <paramref name="ModifierNeedsFixation"/> is set this is the
    /// producer's <em>preferred</em> (first-returned) modifier rather than a final value.
    /// </param>
    /// <param name="ModifierNeedsFixation">
    /// True when the producer returned more than one modifier (it honoured <c>DONT_FIXATE</c>) and the
    /// negotiation must be fixated by re-offering a single modifier.
    /// </param>
    // We deliberately keep ONLY the preferred modifier + a "needs fixation" flag, never the full
    // returned set. The set is never needed: the consumer offers exactly the modifiers its GPU can
    // import, so whatever subset the producer returns is entirely importable and the first one is
    // always a safe choice to fixate on. Storing just a scalar keeps this a stack-friendly value type
    // (a record struct held in a field) with zero heap allocation per negotiation - materialising the
    // set into a long[] would allocate for no benefit and break the library's zero-copy discipline.
    internal readonly record struct VideoFormatInfo(
        PixelFormat Format, int Width, int Height, VideoColorInfo Color,
        ulong Modifier = DrmFormatModifier.Invalid,
        bool ModifierNeedsFixation = false);

    /// <summary>The largest parameter pod this library will parse, in bytes.</summary>
    /// <remarks>
    /// A ceiling, not a format limit, and the tightest one that still fits real traffic. The pods
    /// that reach this parser are negotiated Format objects: a handful of properties, or an
    /// EnumFormat listing every format and modifier a GPU supports, which is a few kilobytes at the
    /// outside.
    /// <para>
    /// It cannot make the read correct. A <c>spa_pod*</c> carries no allocation length - the size in
    /// its own header is how PipeWire itself measures every pod - so a size that is wrong but
    /// plausible is indistinguishable from a true one, here and in the C code alike. What the cap
    /// does is bound how far a wrong one reaches, and every access inside it is checked against the
    /// declared size by the reader, so the exposure is that bound and nothing more.
    /// </para>
    /// </remarks>
    internal const uint MaxParamPodBytes = 64 * 1024;

    /// <summary>The negotiated audio format extracted from a Format pod.</summary>
    internal readonly record struct AudioFormatInfo(AudioSampleFormat Format, int SampleRate, int Channels);

    /// <summary>Parses a Format object pod into video format/size/color. Unset fields keep their incoming value.</summary>
    internal static unsafe VideoFormatInfo ParseVideoFormat(spa_pod* param, VideoFormatInfo current)
    {
        var (fmt, w, h, color) = (current.Format, current.Width, current.Height, current.Color);
        var (range, matrix, transfer, primaries) = (color.Range, color.Matrix, color.Transfer, color.Primaries);
        // Not carried over from the incoming format. An absent VideoModifier means the producer
        // negotiated host memory, and keeping the last DMA-BUF modifier there makes the fallback
        // look like a still-active GPU path to everything downstream that imports by modifier.
        ulong modifier = DrmFormatModifier.Invalid;
        bool modifierNeedsFixation = false;

        // Null means the parameter was withdrawn, not that it is empty; there is nothing to read
        // and dereferencing it is a fault in a callback the loop thread cannot survive.
        if (param is null) return current;

        // The size is the producer's word about memory this process does not own the length of.
        // Overflow is not the only lie available: a size within int range but far past the real
        // allocation makes a span the reader walks off the end of, and the fault lands in the
        // middle of a stream callback. Nothing legitimate is anywhere near the cap.
        uint size = ((uint*)param)[0];
        if (size > MaxParamPodBytes) return current;

        var pod = new ReadOnlySpan<byte>(param, 8 + (int)size);
        var reader = new SpaPodReader(pod);
        if (reader.EnterObject(out uint objType, out _, out _) && (SpaType)objType == SpaType.ObjectFormat)
        {
            while (reader.TryReadProperty(out SpaKey key, out uint propFlags, out var value))
            {
                try
                {
                    if (key == SpaFormat.VideoFormat)
                        fmt = FromSpaVideoFormat((SpaVideoFormat)ReadId(ref value));
                    else if (key == SpaFormat.VideoModifier)
                    {
                        // The producer returns either a single fixated modifier or, when it honoured
                        // DONT_FIXATE, a choice of several it supports. We only need the preferred one
                        // (to use/fixate on) - read in place with no allocation. See VideoFormatInfo
                        // for why the full set is intentionally discarded.
                        //
                        // Whether it still needs fixating is the producer's own DONT_FIXATE flag,
                        // not the number of values. A Choice(Enum) is { default, alt... }, so the
                        // preferred value appears twice and a single-modifier offer counts two -
                        // reading the count as the alternative count fixates offers that are
                        // already fixed. Upstream reads the flag, and so does this.
                        if (value.TryReadModifier(out long first, out int n) && n > 0)
                        {
                            modifier = (ulong)first;
                            modifierNeedsFixation = (propFlags & SpaPodPropFlag.DontFixate) != 0;
                        }
                    }
                    else if (key == SpaFormat.VideoSize)
                    {
                        var (rw, rh) = value.TryUnwrapChoice(out var i) ? i.ReadRectangle() : value.ReadRectangle();

                        // Dimensions arrive as uint32 and are used as int everywhere below. A value
                        // past int.MaxValue casts negative and every size derived from it is wrong;
                        // a zero is a frame with no pixels. Neither is a format worth adopting, so
                        // the previous one is kept.
                        if (rw is > 0 and <= int.MaxValue && rh is > 0 and <= int.MaxValue)
                        {
                            w = (int)rw;
                            h = (int)rh;
                        }
                    }
                    else if (key == SpaFormat.VideoColorRange)
                        range = MapColorRange((SpaVideoColorRange)ReadId(ref value));
                    else if (key == SpaFormat.VideoColorMatrix)
                        matrix = MapColorMatrix((SpaVideoColorMatrix)ReadId(ref value));
                    else if (key == SpaFormat.VideoTransferFunction)
                        transfer = MapTransfer((SpaVideoTransferFunction)ReadId(ref value));
                    else if (key == SpaFormat.VideoColorPrimaries)
                        primaries = MapPrimaries((SpaVideoColorPrimaries)ReadId(ref value));
                }
                catch (InvalidOperationException) { /* malformed property - skip */ }
            }
        }
        return new VideoFormatInfo(fmt, w, h, new VideoColorInfo(range, matrix, transfer, primaries),
            modifier, modifierNeedsFixation);

        static uint ReadId(ref SpaPodReader v) => v.TryUnwrapChoice(out var inner) ? inner.ReadId() : v.ReadId();
    }

    /// <summary>Parses a Format object pod into audio format/rate/channels.</summary>
    internal static unsafe AudioFormatInfo ParseAudioFormat(spa_pod* param, AudioFormatInfo current)
    {
        var (fmt, rate, ch) = (current.Format, current.SampleRate, current.Channels);

        // Null means the parameter was withdrawn, not that it is empty; there is nothing to read
        // and dereferencing it is a fault in a callback the loop thread cannot survive.
        if (param is null) return current;

        // The size is the producer's word about memory this process does not own the length of.
        // Overflow is not the only lie available: a size within int range but far past the real
        // allocation makes a span the reader walks off the end of, and the fault lands in the
        // middle of a stream callback. Nothing legitimate is anywhere near the cap.
        uint size = ((uint*)param)[0];
        if (size > MaxParamPodBytes) return current;

        var pod = new ReadOnlySpan<byte>(param, 8 + (int)size);
        var reader = new SpaPodReader(pod);
        if (reader.EnterObject(out uint objType, out _, out _) && (SpaType)objType == SpaType.ObjectFormat)
        {
            while (reader.TryReadProperty(out SpaKey key, out var value))
            {
                try
                {
                    if (key == SpaFormat.AudioFormat)
                        fmt = FromSpaAudioFormat((value.TryUnwrapChoice(out var i) ? i.ReadId() : value.ReadId()).As<SpaAudioFormat>());
                    else if (key == SpaFormat.AudioRate)
                        rate = value.TryUnwrapChoice(out var i) ? i.ReadInt() : value.ReadInt();
                    else if (key == SpaFormat.AudioChannels)
                        ch = value.TryUnwrapChoice(out var i) ? i.ReadInt() : value.ReadInt();
                }
                catch (InvalidOperationException) { /* malformed property - skip */ }
            }
        }
        return new AudioFormatInfo(fmt, rate, ch);
    }

    // - Video -

    /// <summary>
    /// Writes a video EnumFormat object. When <paramref name="formats"/> is empty a
    /// broad default set is offered; otherwise exactly the requested formats.
    /// </summary>
    /// <param name="buf">Destination buffer for the POD bytes.</param>
    /// <param name="formats">Pixel formats to offer (empty = broad default set).</param>
    /// <param name="defaultWidth">Default/preferred width.</param>
    /// <param name="defaultHeight">Default/preferred height.</param>
    /// <param name="defaultFrameRate">Default/preferred frame rate (per second).</param>
    /// <param name="fixedSize">True to offer a single fixed size/rate; false to offer a range.</param>
    /// <param name="modifiers">
    /// DRM format modifiers to offer for zero-copy dmabuf. When non-empty, a single
    /// <paramref name="formats"/> entry should be supplied (modifiers are per-format). The first
    /// modifier is preferred; the rest are alternatives.
    /// </param>
    /// <param name="fixateModifier">
    /// On the first pass pass <see langword="false"/>: the modifier choice is offered with
    /// <c>DONT_FIXATE</c> so the producer narrows it without collapsing. Once the consumer has
    /// chosen a single modifier its GPU supports, re-submit with <see langword="true"/> (no
    /// <c>DONT_FIXATE</c>) to fixate the negotiation.
    /// </param>
    internal static int WriteVideoFormat(
        Span<byte> buf,
        ReadOnlySpan<PixelFormat> formats,
        uint defaultWidth, uint defaultHeight, uint defaultFrameRate,
        bool fixedSize,
        ReadOnlySpan<long> modifiers = default,
        bool fixateModifier = false)
    {
        var b = new SpaPodBuilder(buf);
        b.PushObject(SpaType.ObjectFormat, SpaParamType.EnumFormat);
        b.AddId(SpaFormat.MediaType,    SpaMediaType.Video);
        b.AddId(SpaFormat.MediaSubtype, SpaMediaSubtype.Raw);

        if (formats.IsEmpty)
        {
            // Every format this library can decode. A supported format left out here works only
            // when the caller names it explicitly, which reads as the producer not offering it.
            b.AddChoiceEnum(SpaFormat.VideoFormat,
                SpaVideoFormat.Bgra, SpaVideoFormat.Rgba,
                SpaVideoFormat.Bgrx, SpaVideoFormat.Rgbx,
                SpaVideoFormat.Yuy2, SpaVideoFormat.I420,
                SpaVideoFormat.Nv12);
        }
        else if (formats.Length == 1)
        {
            b.AddId(SpaFormat.VideoFormat, ToSpaVideoFormat(formats[0]));
        }
        else
        {
            Span<SpaIdValue> ids = stackalloc SpaIdValue[formats.Length];
            for (int i = 0; i < formats.Length; i++)
                ids[i] = ToSpaVideoFormat(formats[i]);
            b.AddChoiceEnum(SpaFormat.VideoFormat, ids);
        }

        // The modifier property must follow the format and precede size (PipeWire convention).
        // First pass (negotiation): a MANDATORY|DONT_FIXATE Choice Enum so the peer narrows the modifier set
        // without collapsing it. Fixation pass: a single MANDATORY plain Long (NOT a choice) - the modifier is
        // settled. This mirrors pipewire's video-src-fixate.c (build_format uses a choice; fixate_format uses a
        // plain long); a fixated modifier written as a 1-entry choice would read back as "still needs fixation".
        if (!modifiers.IsEmpty)
        {
            if (fixateModifier)
            {
                b.AddLong(SpaFormat.VideoModifier, modifiers[0], SpaPodPropFlag.Mandatory);
            }
            else
            {
                b.AddChoiceEnumLong(SpaFormat.VideoModifier, modifiers,
                    SpaPodPropFlag.Mandatory | SpaPodPropFlag.DontFixate);
            }
        }

        if (fixedSize)
        {
            b.AddRectangle(SpaFormat.VideoSize, defaultWidth, defaultHeight);
            b.AddFraction(SpaFormat.VideoFramerate, defaultFrameRate, 1);
        }
        else
        {
            b.AddChoiceRangeRectangle(SpaFormat.VideoSize,
                defaultWidth, defaultHeight, 1, 1, 8192, 8192);
            b.AddChoiceRangeFraction(SpaFormat.VideoFramerate,
                defaultFrameRate, 1, 0, 1, 1000, 1);
        }

        return b.GetPod().Length;
    }

    internal static SpaVideoFormat ToSpaVideoFormat(PixelFormat fmt) => fmt switch
    {
        PixelFormat.Rgba   => SpaVideoFormat.Rgba,
        PixelFormat.Bgra   => SpaVideoFormat.Bgra,
        PixelFormat.Rgbx   => SpaVideoFormat.Rgbx,
        PixelFormat.Bgrx   => SpaVideoFormat.Bgrx,
        PixelFormat.Yuyv   => SpaVideoFormat.Yuy2,
        PixelFormat.Yuv420 => SpaVideoFormat.I420,
        PixelFormat.Nv12   => SpaVideoFormat.Nv12,

        // Unknown is what a negotiation reports when the producer named a format this version does
        // not model; there is nothing to ask for. Falling through to BGRA would offer the wrong format
        // and decode whatever comes back as BGRA, which renders as a tinted, sheared image
        // rather than failing.
        _ => throw new ArgumentOutOfRangeException(
            nameof(fmt), fmt, "not a pixel format that can be offered"),
    };

    internal static PixelFormat FromSpaVideoFormat(SpaVideoFormat spa) => spa switch
    {
        SpaVideoFormat.Rgba => PixelFormat.Rgba,
        SpaVideoFormat.Bgra => PixelFormat.Bgra,
        SpaVideoFormat.Rgbx => PixelFormat.Rgbx,
        SpaVideoFormat.Bgrx => PixelFormat.Bgrx,
        SpaVideoFormat.Yuy2 => PixelFormat.Yuyv,
        SpaVideoFormat.I420 => PixelFormat.Yuv420,
        SpaVideoFormat.Nv12 => PixelFormat.Nv12,
        // Not BGRA. Reinterpreting an unrecognised layout as BGRA produces a plausible-looking but
        // wrong image, which is far harder to notice than a format that is simply unsupported.
        _                                  => PixelFormat.Unknown,
    };

    /// <summary>
    /// Number of dmabuf planes (spa_data blocks) for a format: packed RGB and YUY2 are one plane;
    /// NV12 is two (Y, interleaved UV); I420 is three (Y, U, V). Planes may share one fd at different
    /// offsets, but each still occupies its own block so PipeWire allocates the right buffer shape.
    /// </summary>
    internal static int PlaneCount(PixelFormat fmt) => fmt switch
    {
        PixelFormat.Nv12   => 2,
        PixelFormat.Yuv420 => 3,
        _                  => 1,
    };

    /// <summary>Bytes per pixel in the primary plane (planar formats report the Y-plane stride unit).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The format is not one this version models.</exception>
    /// <remarks>
    /// No four-byte default. An unmodelled format is not necessarily packed 32bpp, and guessing that
    /// it is sizes every buffer and stride derived from it wrongly rather than reporting that the
    /// format is not understood.
    /// </remarks>
    internal static int BytesPerPixel(PixelFormat fmt) => fmt switch
    {
        PixelFormat.Yuv420 => 1,
        PixelFormat.Nv12   => 1,
        PixelFormat.Yuyv   => 2,
        PixelFormat.Rgba or PixelFormat.Bgra or PixelFormat.Rgbx or PixelFormat.Bgrx => 4,
        _ => throw new ArgumentOutOfRangeException(
            nameof(fmt), fmt, "no known bytes-per-pixel for this format"),
    };

    // - Audio -

    internal static int WriteAudioFormat(
        Span<byte> buf, AudioSampleFormat format, int sampleRate, int channels)
    {
        var b = new SpaPodBuilder(buf);
        b.PushObject(SpaType.ObjectFormat, SpaParamType.EnumFormat);
        b.AddId(SpaFormat.MediaType,    SpaMediaType.Audio);
        b.AddId(SpaFormat.MediaSubtype, SpaMediaSubtype.Raw);
        b.AddId(SpaFormat.AudioFormat,       ToSpaAudioFormat(format));
        b.AddInt(SpaFormat.AudioRate,        sampleRate);
        b.AddInt(SpaFormat.AudioChannels,    channels);
        return b.GetPod().Length;
    }

    internal static SpaAudioFormat ToSpaAudioFormat(AudioSampleFormat fmt) => fmt switch
    {
        AudioSampleFormat.U8    => SpaAudioFormat.U8,
        AudioSampleFormat.S16Le => SpaAudioFormat.S16Le,
        AudioSampleFormat.S24Le => SpaAudioFormat.S24Le,
        AudioSampleFormat.S32Le => SpaAudioFormat.S32Le,
        AudioSampleFormat.F32Le => SpaAudioFormat.F32Le,
        AudioSampleFormat.S24_32Le => SpaAudioFormat.S24_32Le,
        AudioSampleFormat.F64Le => SpaAudioFormat.F64Le,

        // Unknown is what a negotiation reports, never something to offer: there is nothing to ask
        // for. Anything else is a caller passing a value the enum does not define.
        _ => throw new ArgumentOutOfRangeException(nameof(fmt), fmt, "not a format that can be offered"),
    };

    // - Color enum mapping -
    // SPA's enum numbering is not contiguous with our public enums, so map by the
    // generated enum values explicitly (a plain cast would be wrong).

    internal static VideoColorRange MapColorRange(SpaVideoColorRange spa) => spa switch
    {
        SpaVideoColorRange.Full  => VideoColorRange.Full_0_255,
        SpaVideoColorRange.Limited => VideoColorRange.Limited_16_235,
        _                                                                      => VideoColorRange.Unknown,
    };

    internal static VideoColorMatrix MapColorMatrix(SpaVideoColorMatrix spa) => spa switch
    {
        SpaVideoColorMatrix.Rgb    => VideoColorMatrix.Rgb,
        SpaVideoColorMatrix.Bt709  => VideoColorMatrix.Bt709,
        SpaVideoColorMatrix.Bt601  => VideoColorMatrix.Bt601,
        SpaVideoColorMatrix.Bt2020 => VideoColorMatrix.Bt2020,
        _                                                                        => VideoColorMatrix.Unknown,
    };

    internal static VideoTransferFunction MapTransfer(SpaVideoTransferFunction spa) => spa switch
    {
        SpaVideoTransferFunction.Gamma22   => VideoTransferFunction.Gamma22,
        SpaVideoTransferFunction.Bt709     => VideoTransferFunction.Bt709,
        SpaVideoTransferFunction.Srgb      => VideoTransferFunction.Srgb,
        SpaVideoTransferFunction.Bt2020_12 => VideoTransferFunction.Bt2020_12,
        _                                                                            => VideoTransferFunction.Unknown,
    };

    internal static VideoColorPrimaries MapPrimaries(SpaVideoColorPrimaries spa) => spa switch
    {
        SpaVideoColorPrimaries.Bt709  => VideoColorPrimaries.Bt709,
        SpaVideoColorPrimaries.Bt2020 => VideoColorPrimaries.Bt2020,
        _                                                                              => VideoColorPrimaries.Unknown,
    };

    internal static AudioSampleFormat FromSpaAudioFormat(SpaAudioFormat spa) => spa switch
    {
        SpaAudioFormat.U8    => AudioSampleFormat.U8,
        SpaAudioFormat.S16Le => AudioSampleFormat.S16Le,
        SpaAudioFormat.S24Le => AudioSampleFormat.S24Le,
        SpaAudioFormat.S32Le => AudioSampleFormat.S32Le,
        SpaAudioFormat.F32Le => AudioSampleFormat.F32Le,
        SpaAudioFormat.S24_32Le => AudioSampleFormat.S24_32Le,
        SpaAudioFormat.F64Le => AudioSampleFormat.F64Le,

        // Not F32Le. Claiming a format the producer did not negotiate makes the consumer read the
        // wrong number of bytes per sample and every channel after the first is offset: audio that
        // plays, sounds wrong, and blames the device.
        _ => AudioSampleFormat.Unknown,
    };
}
