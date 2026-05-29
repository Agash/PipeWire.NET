using PipeWire.NET.Generated;

namespace PipeWire.NET.Spa;

/// <summary>
/// Centralizes SPA format-pod construction and the managed-to-SPA format-enum mappings
/// shared by the video/audio capture and output stream wrappers.
/// </summary>
internal static class SpaFormat
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
        b.PushObject(SpaType.ObjectParamMeta, SpaParam.Meta);
        b.AddId(SpaParamMeta.Type, SpaMetaType.Header);
        b.AddInt(SpaParamMeta.Size, sizeof(spa_meta_header));
        return b.GetPod().Length;
    }

    // - Param: buffer requirements (declares accepted data types incl. DMA-BUF) -

    /// <summary>
    /// Writes a ParamBuffers object advertising the accepted buffer data types (and a buffer
    /// count). Including <c>SPA_DATA_DmaBuf</c> in <paramref name="dataTypes"/> opts into
    /// zero-copy GPU buffers; the producer picks one of the offered types, falling back to host
    /// memory when it can't provide DMA-BUF.
    /// </summary>
    /// <remarks>
    /// Follows PipeWire's canonical buffer param (buffers/size/stride/dataType). Size and stride
    /// must be correct for the negotiated layout - see <see cref="VideoStride"/> /
    /// <see cref="VideoImageSize"/>, which handle packed and planar formats.
    /// </remarks>
    internal static int WriteVideoBuffersParam(Span<byte> buf, int size, int stride, int dataTypes)
    {
        var b = new SpaPodBuilder(buf);
        b.PushObject(SpaType.ObjectParamBuffers, SpaParam.Buffers);
        b.AddChoiceRangeInt(SpaParamBuffers.Buffers, 8, 2, 16);
        b.AddInt(SpaParamBuffers.Blocks, 1);
        b.AddInt(SpaParamBuffers.Size, size);
        b.AddInt(SpaParamBuffers.Stride, stride);
        b.AddChoiceFlagsInt(SpaParamBuffers.DataType, dataTypes);
        return b.GetPod().Length;
    }

    /// <summary>Primary-plane stride (bytes per row) for the negotiated format.</summary>
    internal static int VideoStride(PixelFormat fmt, int width) => fmt switch
    {
        PixelFormat.Yuv420 => width,        // I420 Y-plane stride
        PixelFormat.Yuyv   => width * 2,
        _                  => width * 4,    // packed 32bpp (BGRA/RGBA/BGRx/RGBx)
    };

    /// <summary>Total buffer size in bytes for the negotiated format (incl. planar chroma planes).</summary>
    internal static int VideoImageSize(PixelFormat fmt, int width, int height) => fmt switch
    {
        PixelFormat.Yuv420 => width * height * 3 / 2,   // Y + U/4 + V/4
        PixelFormat.Yuyv   => width * height * 2,
        _                  => width * height * 4,
    };

    /// <summary>Accepted capture data types: host memory + DMA-BUF (zero-copy GPU).</summary>
    internal static int VideoCaptureDataTypeMask =>
        (1 << (int)SpaType.DataMemPtr) | (1 << (int)SpaType.DataMemFd) | (1 << (int)SpaType.DataDmaBuf);

    // - Buffer metadata -

    /// <summary>Maps a raw <c>spa_data.type</c> to <see cref="PipeWireBufferType"/>.</summary>
    internal static PipeWireBufferType ToBufferType(uint spaDataType) => spaDataType switch
    {
        _ when spaDataType == SpaType.DataMemPtr => PipeWireBufferType.MemPtr,
        _ when spaDataType == SpaType.DataMemFd  => PipeWireBufferType.MemFd,
        _ when spaDataType == SpaType.DataDmaBuf => PipeWireBufferType.DmaBuf,
        _                                        => PipeWireBufferType.Unknown,
    };

    /// <summary>
    /// Finds the presentation timestamp (ns) from a buffer's SPA_META_Header, or -1 if absent.
    /// </summary>
    /// <remarks>
    /// Video producers populate this; PipeWire audio typically does NOT carry a per-buffer
    /// header PTS (audio timing is derived from the graph clock + sample position), so audio
    /// frames usually report -1 here.
    /// </remarks>
    internal static unsafe long FindPresentationTimeNs(spa_buffer* buf)
    {
        if (buf is null || buf->metas is null) return -1;
        uint headerType = (uint)spa_meta_type.SPA_META_Header;
        for (uint i = 0; i < buf->n_metas; i++)
        {
            spa_meta* m = &buf->metas[i];
            if (m->type == headerType && m->data is not null && m->size >= (uint)sizeof(spa_meta_header))
                return (long)((spa_meta_header*)m->data)->pts;
        }
        return -1;
    }

    // - Format-pod parsing (param_changed) -

    /// <summary>The negotiated video format extracted from a Format pod.</summary>
    internal readonly record struct VideoFormatInfo(
        PixelFormat Format, int Width, int Height, VideoColorInfo Color);

    /// <summary>The negotiated audio format extracted from a Format pod.</summary>
    internal readonly record struct AudioFormatInfo(AudioSampleFormat Format, int SampleRate, int Channels);

    /// <summary>Parses a Format object pod into video format/size/color. Unset fields keep their incoming value.</summary>
    internal static unsafe VideoFormatInfo ParseVideoFormat(spa_pod* param, VideoFormatInfo current)
    {
        var (fmt, w, h, color) = (current.Format, current.Width, current.Height, current.Color);
        var (range, matrix, transfer, primaries) = (color.Range, color.Matrix, color.Transfer, color.Primaries);

        uint size = ((uint*)param)[0];
        var pod = new ReadOnlySpan<byte>(param, 8 + (int)size);
        var reader = new SpaPodReader(pod);
        if (reader.EnterObject(out uint objType, out _, out _) && objType == SpaType.ObjectFormat)
        {
            while (reader.TryReadProperty(out uint key, out var value))
            {
                try
                {
                    if (key == SpaFormatVideo.Format)
                        fmt = FromSpaVideoFormat(ReadId(ref value));
                    else if (key == SpaFormatVideo.Size)
                    {
                        var (rw, rh) = value.TryUnwrapChoice(out var i) ? i.ReadRectangle() : value.ReadRectangle();
                        w = (int)rw; h = (int)rh;
                    }
                    else if (key == SpaFormatVideo.ColorRange)
                        range = MapColorRange(ReadId(ref value));
                    else if (key == SpaFormatVideo.ColorMatrix)
                        matrix = MapColorMatrix(ReadId(ref value));
                    else if (key == SpaFormatVideo.TransferFunction)
                        transfer = MapTransfer(ReadId(ref value));
                    else if (key == SpaFormatVideo.ColorPrimaries)
                        primaries = MapPrimaries(ReadId(ref value));
                }
                catch (InvalidOperationException) { /* malformed property - skip */ }
            }
        }
        return new VideoFormatInfo(fmt, w, h, new VideoColorInfo(range, matrix, transfer, primaries));

        static uint ReadId(ref SpaPodReader v) => v.TryUnwrapChoice(out var inner) ? inner.ReadId() : v.ReadId();
    }

    /// <summary>Parses a Format object pod into audio format/rate/channels.</summary>
    internal static unsafe AudioFormatInfo ParseAudioFormat(spa_pod* param, AudioFormatInfo current)
    {
        var (fmt, rate, ch) = (current.Format, current.SampleRate, current.Channels);

        uint size = ((uint*)param)[0];
        var pod = new ReadOnlySpan<byte>(param, 8 + (int)size);
        var reader = new SpaPodReader(pod);
        if (reader.EnterObject(out uint objType, out _, out _) && objType == SpaType.ObjectFormat)
        {
            while (reader.TryReadProperty(out uint key, out var value))
            {
                try
                {
                    if (key == SpaFormatAudio.Format)
                        fmt = FromSpaAudioFormat(value.TryUnwrapChoice(out var i) ? i.ReadId() : value.ReadId());
                    else if (key == SpaFormatAudio.Rate)
                        rate = value.TryUnwrapChoice(out var i) ? i.ReadInt() : value.ReadInt();
                    else if (key == SpaFormatAudio.Channels)
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
    internal static int WriteVideoFormat(
        Span<byte> buf,
        ReadOnlySpan<PixelFormat> formats,
        uint defaultWidth, uint defaultHeight, uint defaultFrameRate,
        bool fixedSize)
    {
        var b = new SpaPodBuilder(buf);
        b.PushObject(SpaType.ObjectFormat, SpaParam.EnumFormat);
        b.AddId(SpaFormatVideo.MediaType,    SpaMediaType.Video);
        b.AddId(SpaFormatVideo.MediaSubtype, SpaMediaSubtype.Raw);

        if (formats.IsEmpty)
        {
            b.AddChoiceEnum(SpaFormatVideo.Format,
                SpaVideoFormat.BGRA, SpaVideoFormat.RGBA,
                SpaVideoFormat.BGRx, SpaVideoFormat.RGBx,
                SpaVideoFormat.YUY2, SpaVideoFormat.I420);
        }
        else if (formats.Length == 1)
        {
            b.AddId(SpaFormatVideo.Format, ToSpaVideoFormat(formats[0]));
        }
        else
        {
            Span<uint> ids = stackalloc uint[formats.Length];
            for (int i = 0; i < formats.Length; i++)
                ids[i] = ToSpaVideoFormat(formats[i]);
            b.AddChoiceEnum(SpaFormatVideo.Format, ids);
        }

        if (fixedSize)
        {
            b.AddRectangle(SpaFormatVideo.Size, defaultWidth, defaultHeight);
            b.AddFraction(SpaFormatVideo.Framerate, defaultFrameRate, 1);
        }
        else
        {
            b.AddChoiceRangeRectangle(SpaFormatVideo.Size,
                defaultWidth, defaultHeight, 1, 1, 8192, 8192);
            b.AddChoiceRangeFraction(SpaFormatVideo.Framerate,
                defaultFrameRate, 1, 0, 1, 1000, 1);
        }

        return b.GetPod().Length;
    }

    internal static uint ToSpaVideoFormat(PixelFormat fmt) => fmt switch
    {
        PixelFormat.Rgba   => SpaVideoFormat.RGBA,
        PixelFormat.Bgra   => SpaVideoFormat.BGRA,
        PixelFormat.Rgbx   => SpaVideoFormat.RGBx,
        PixelFormat.Bgrx   => SpaVideoFormat.BGRx,
        PixelFormat.Yuyv   => SpaVideoFormat.YUY2,
        PixelFormat.Yuv420 => SpaVideoFormat.I420,
        _                  => SpaVideoFormat.BGRA,
    };

    internal static PixelFormat FromSpaVideoFormat(uint spa) => spa switch
    {
        _ when spa == SpaVideoFormat.RGBA => PixelFormat.Rgba,
        _ when spa == SpaVideoFormat.BGRA => PixelFormat.Bgra,
        _ when spa == SpaVideoFormat.RGBx => PixelFormat.Rgbx,
        _ when spa == SpaVideoFormat.BGRx => PixelFormat.Bgrx,
        _ when spa == SpaVideoFormat.YUY2 => PixelFormat.Yuyv,
        _ when spa == SpaVideoFormat.I420 => PixelFormat.Yuv420,
        _                                  => PixelFormat.Bgra,
    };

    /// <summary>Bytes per pixel in the primary plane (planar formats report the Y-plane stride unit).</summary>
    internal static int BytesPerPixel(PixelFormat fmt) => fmt switch
    {
        PixelFormat.Yuv420 => 1,
        PixelFormat.Yuyv   => 2,
        _                  => 4,
    };

    // - Audio -

    internal static int WriteAudioFormat(
        Span<byte> buf, AudioSampleFormat format, int sampleRate, int channels)
    {
        var b = new SpaPodBuilder(buf);
        b.PushObject(SpaType.ObjectFormat, SpaParam.EnumFormat);
        b.AddId(SpaFormatVideo.MediaType,    SpaMediaType.Audio);
        b.AddId(SpaFormatVideo.MediaSubtype, SpaMediaSubtype.Raw);
        b.AddId(SpaFormatAudio.Format,       ToSpaAudioFormat(format));
        b.AddInt(SpaFormatAudio.Rate,        sampleRate);
        b.AddInt(SpaFormatAudio.Channels,    channels);
        return b.GetPod().Length;
    }

    internal static uint ToSpaAudioFormat(AudioSampleFormat fmt) => fmt switch
    {
        AudioSampleFormat.U8    => SpaAudioFormat.U8,
        AudioSampleFormat.S16Le => SpaAudioFormat.S16Le,
        AudioSampleFormat.S24Le => SpaAudioFormat.S24Le,
        AudioSampleFormat.S32Le => SpaAudioFormat.S32Le,
        AudioSampleFormat.F32Le => SpaAudioFormat.F32Le,
        _                       => SpaAudioFormat.F32Le,
    };

    // - Color enum mapping -
    // SPA's enum numbering is not contiguous with our public enums, so map by the
    // generated enum values explicitly (a plain cast would be wrong).

    internal static VideoColorRange MapColorRange(uint spa) => spa switch
    {
        _ when spa == (uint)spa_video_color_range.SPA_VIDEO_COLOR_RANGE_0_255  => VideoColorRange.Full_0_255,
        _ when spa == (uint)spa_video_color_range.SPA_VIDEO_COLOR_RANGE_16_235 => VideoColorRange.Limited_16_235,
        _                                                                      => VideoColorRange.Unknown,
    };

    internal static VideoColorMatrix MapColorMatrix(uint spa) => spa switch
    {
        _ when spa == (uint)spa_video_color_matrix.SPA_VIDEO_COLOR_MATRIX_RGB    => VideoColorMatrix.Rgb,
        _ when spa == (uint)spa_video_color_matrix.SPA_VIDEO_COLOR_MATRIX_BT709  => VideoColorMatrix.Bt709,
        _ when spa == (uint)spa_video_color_matrix.SPA_VIDEO_COLOR_MATRIX_BT601  => VideoColorMatrix.Bt601,
        _ when spa == (uint)spa_video_color_matrix.SPA_VIDEO_COLOR_MATRIX_BT2020 => VideoColorMatrix.Bt2020,
        _                                                                        => VideoColorMatrix.Unknown,
    };

    internal static VideoTransferFunction MapTransfer(uint spa) => spa switch
    {
        _ when spa == (uint)spa_video_transfer_function.SPA_VIDEO_TRANSFER_GAMMA22   => VideoTransferFunction.Gamma22,
        _ when spa == (uint)spa_video_transfer_function.SPA_VIDEO_TRANSFER_BT709     => VideoTransferFunction.Bt709,
        _ when spa == (uint)spa_video_transfer_function.SPA_VIDEO_TRANSFER_SRGB      => VideoTransferFunction.Srgb,
        _ when spa == (uint)spa_video_transfer_function.SPA_VIDEO_TRANSFER_BT2020_12 => VideoTransferFunction.Bt2020_12,
        _                                                                            => VideoTransferFunction.Unknown,
    };

    internal static VideoColorPrimaries MapPrimaries(uint spa) => spa switch
    {
        _ when spa == (uint)spa_video_color_primaries.SPA_VIDEO_COLOR_PRIMARIES_BT709  => VideoColorPrimaries.Bt709,
        _ when spa == (uint)spa_video_color_primaries.SPA_VIDEO_COLOR_PRIMARIES_BT2020 => VideoColorPrimaries.Bt2020,
        _                                                                              => VideoColorPrimaries.Unknown,
    };

    internal static AudioSampleFormat FromSpaAudioFormat(uint spa) => spa switch
    {
        _ when spa == SpaAudioFormat.U8    => AudioSampleFormat.U8,
        _ when spa == SpaAudioFormat.S16Le => AudioSampleFormat.S16Le,
        _ when spa == SpaAudioFormat.S24Le => AudioSampleFormat.S24Le,
        _ when spa == SpaAudioFormat.S32Le => AudioSampleFormat.S32Le,
        _ when spa == SpaAudioFormat.F32Le => AudioSampleFormat.F32Le,
        _                                   => AudioSampleFormat.F32Le,
    };
}
