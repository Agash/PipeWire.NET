using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Interop;

namespace PipeWire.NET.Tests;

/// <summary>
/// The layout of the generated structs, against the ABI the installed daemon actually uses.
/// </summary>
/// <remarks>
/// Bindings are generated from whatever headers were installed at the time, and a header change
/// that reorders or inserts a field still compiles perfectly - the mismatch only appears at runtime
/// as a callback that fires with nonsense, or a buffer read at the wrong offset. These pin the
/// shapes that carry pointers and function pointers, where a drift is silent and fatal rather than
/// merely wrong.
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed unsafe class NativeAbiTests
{
    [TestMethod]
    public void EventTables_StartWithTheirVersionField()
    {
        // Every spa events struct begins with a uint32 version the daemon reads before dispatching
        // anything. A field inserted ahead of it makes the daemon read a function pointer as the
        // version and then refuse - or call through whatever it found.
        Assert.AreEqual(0, (int)Marshal.OffsetOf<pw_registry_events>(nameof(pw_registry_events.version)));
        Assert.AreEqual(0, (int)Marshal.OffsetOf<pw_core_events>(nameof(pw_core_events.version)));
        Assert.AreEqual(0, (int)Marshal.OffsetOf<pw_node_events>(nameof(pw_node_events.version)));
        Assert.AreEqual(0, (int)Marshal.OffsetOf<pw_metadata_events>(nameof(pw_metadata_events.version)));
        Assert.AreEqual(0, (int)Marshal.OffsetOf<pw_proxy_events>(nameof(pw_proxy_events.version)));
        Assert.AreEqual(0, (int)Marshal.OffsetOf<pw_profiler_events>(nameof(pw_profiler_events.version)));
    }

    [TestMethod]
    public void MethodTables_StartWithTheirVersionField()
    {
        Assert.AreEqual(0, (int)Marshal.OffsetOf<pw_metadata_methods>(nameof(pw_metadata_methods.version)));
        Assert.AreEqual(0, (int)Marshal.OffsetOf<pw_profiler_methods>(nameof(pw_profiler_methods.version)));
        Assert.AreEqual(0, (int)Marshal.OffsetOf<pw_security_context_methods>(
            nameof(pw_security_context_methods.version)));
    }

    [TestMethod]
    public void FunctionPointerFields_ArePointerSizedAndAligned()
    {
        // A version field followed by pointers means the first pointer sits at the platform's
        // pointer alignment, not at four bytes. Getting that wrong shifts every callback by one
        // slot, which dispatches the wrong function rather than failing.
        Assert.AreEqual(sizeof(nint), (int)Marshal.OffsetOf<pw_metadata_methods>(
            nameof(pw_metadata_methods.add_listener)));
        Assert.AreEqual(sizeof(nint), (int)Marshal.OffsetOf<pw_profiler_events>(
            nameof(pw_profiler_events.profile)));
    }

    [TestMethod]
    public void SpaData_KeepsTheLayoutTheBufferPathReadsThrough()
    {
        // Capture reads chunk->offset and chunk->size out of these to build a span over the
        // producer's memory. A drift here reads the wrong bytes as a frame.
        Assert.AreEqual(0, (int)Marshal.OffsetOf<spa_data>(nameof(spa_data.type)));
        Assert.IsTrue((int)Marshal.OffsetOf<spa_data>(nameof(spa_data.maxsize))
                      < (int)Marshal.OffsetOf<spa_data>(nameof(spa_data.data)));
        Assert.IsTrue((int)Marshal.OffsetOf<spa_data>(nameof(spa_data.data))
                      < (int)Marshal.OffsetOf<spa_data>(nameof(spa_data.chunk)));

        Assert.AreEqual(0, (int)Marshal.OffsetOf<spa_chunk>(nameof(spa_chunk.offset)));
        Assert.AreEqual(4, (int)Marshal.OffsetOf<spa_chunk>(nameof(spa_chunk.size)));
        Assert.AreEqual(8, (int)Marshal.OffsetOf<spa_chunk>(nameof(spa_chunk.stride)));
    }

    [TestMethod]
    public void SpaBuffer_KeepsTheCountsAheadOfTheArraysTheyDescribe()
    {
        // Every realtime read starts here: n_datas and datas are read together to reach a plane,
        // and n_metas and metas to find the presentation time. A field inserted between them makes
        // a count describe the wrong array.
        Assert.AreEqual(0, (int)Marshal.OffsetOf<spa_buffer>(nameof(spa_buffer.n_metas)));
        Assert.AreEqual(4, (int)Marshal.OffsetOf<spa_buffer>(nameof(spa_buffer.n_datas)));
        Assert.AreEqual(8, (int)Marshal.OffsetOf<spa_buffer>(nameof(spa_buffer.metas)));
        Assert.AreEqual(8 + sizeof(nint), (int)Marshal.OffsetOf<spa_buffer>(nameof(spa_buffer.datas)));
        Assert.AreEqual(8 + (2 * sizeof(nint)), sizeof(spa_buffer));
    }

    [TestMethod]
    public void PwBuffer_StartsWithTheSpaBufferAndItsUserData()
    {
        // The dmabuf producer stores its pool index in user_data and reads it back on every
        // publish, so an offset drift hands the app a different buffer's surface.
        Assert.AreEqual(0, (int)Marshal.OffsetOf<pw_buffer>(nameof(pw_buffer.buffer)));
        Assert.AreEqual(sizeof(nint), (int)Marshal.OffsetOf<pw_buffer>(nameof(pw_buffer.user_data)));
    }

    [TestMethod]
    public void SpaMeta_AndItsHeader_KeepTheLayoutThePtsIsReadThrough()
    {
        // The metadata walk reads type and size to decide whether the entry is a header big enough
        // to hold one, then reads pts out of it. Three offsets, all silent if they drift.
        Assert.AreEqual(0, (int)Marshal.OffsetOf<spa_meta>(nameof(spa_meta.type)));
        Assert.AreEqual(4, (int)Marshal.OffsetOf<spa_meta>(nameof(spa_meta.size)));
        Assert.AreEqual(8, (int)Marshal.OffsetOf<spa_meta>(nameof(spa_meta.data)));

        Assert.AreEqual(0, (int)Marshal.OffsetOf<spa_meta_header>(nameof(spa_meta_header.flags)));
        Assert.AreEqual(4, (int)Marshal.OffsetOf<spa_meta_header>(nameof(spa_meta_header.offset)));
        Assert.AreEqual(8, (int)Marshal.OffsetOf<spa_meta_header>(nameof(spa_meta_header.pts)));
        Assert.AreEqual(16, (int)Marshal.OffsetOf<spa_meta_header>(nameof(spa_meta_header.dts_offset)));
    }

    [TestMethod]
    public void SpaData_IsTheWholeShapeAndNotJustAnOrdering()
    {
        // The ordering assertion above catches a swap and not an insertion. These are the offsets
        // the capture path actually indexes with.
        Assert.AreEqual(0, (int)Marshal.OffsetOf<spa_data>(nameof(spa_data.type)));
        Assert.AreEqual(4, (int)Marshal.OffsetOf<spa_data>(nameof(spa_data.flags)));
        Assert.AreEqual(8, (int)Marshal.OffsetOf<spa_data>(nameof(spa_data.fd)));
        Assert.AreEqual(16, (int)Marshal.OffsetOf<spa_data>(nameof(spa_data.mapoffset)));
        Assert.AreEqual(20, (int)Marshal.OffsetOf<spa_data>(nameof(spa_data.maxsize)));
        Assert.AreEqual(24, (int)Marshal.OffsetOf<spa_data>(nameof(spa_data.data)));
        Assert.AreEqual(24 + sizeof(nint), (int)Marshal.OffsetOf<spa_data>(nameof(spa_data.chunk)));
    }

    [TestMethod]
    public void TheInfoStructs_KeepTheFieldsTheParsersRead()
    {
        // Each info struct arrives by pointer in a callback and is read field by field. The
        // change_mask in particular decides whether the rest is even valid.
        Assert.AreEqual(0, (int)Marshal.OffsetOf<pw_node_info>(nameof(pw_node_info.id)));
        Assert.IsTrue((int)Marshal.OffsetOf<pw_node_info>(nameof(pw_node_info.change_mask))
                      < (int)Marshal.OffsetOf<pw_node_info>(nameof(pw_node_info.props)));
        Assert.IsTrue((int)Marshal.OffsetOf<pw_node_info>(nameof(pw_node_info.props))
                      < (int)Marshal.OffsetOf<pw_node_info>(nameof(pw_node_info.@params)));

        Assert.AreEqual(0, (int)Marshal.OffsetOf<pw_port_info>(nameof(pw_port_info.id)));
        Assert.AreEqual(4, (int)Marshal.OffsetOf<pw_port_info>(nameof(pw_port_info.direction)));

        Assert.AreEqual(0, (int)Marshal.OffsetOf<pw_link_info>(nameof(pw_link_info.id)));
        Assert.AreEqual(4, (int)Marshal.OffsetOf<pw_link_info>(nameof(pw_link_info.output_node_id)));
        Assert.AreEqual(8, (int)Marshal.OffsetOf<pw_link_info>(nameof(pw_link_info.output_port_id)));
        Assert.AreEqual(12, (int)Marshal.OffsetOf<pw_link_info>(nameof(pw_link_info.input_node_id)));
        Assert.AreEqual(16, (int)Marshal.OffsetOf<pw_link_info>(nameof(pw_link_info.input_port_id)));
    }

    [TestMethod]
    public void SpaPodHeader_IsTwoUnsignedIntsInSizeThenType()
    {
        // The whole parser assumes this: eight bytes of header, size first. It is also what every
        // hand-built pod in the test suite writes.
        Assert.AreEqual(8, sizeof(spa_pod));
        Assert.AreEqual(0, (int)Marshal.OffsetOf<spa_pod>(nameof(spa_pod.size)));
        Assert.AreEqual(4, (int)Marshal.OffsetOf<spa_pod>(nameof(spa_pod.type)));
    }

    [TestMethod]
    public void SpaDictItem_IsAPairOfPointers()
    {
        // Property dictionaries are built by handing the daemon pointers into a caller-owned
        // buffer. If this were anything but two pointers, every created object would carry
        // properties read from the wrong addresses.
        Assert.AreEqual(2 * sizeof(nint), sizeof(spa_dict_item));
        Assert.AreEqual(0, (int)Marshal.OffsetOf<spa_dict_item>(nameof(spa_dict_item.key)));
        Assert.AreEqual(sizeof(nint), (int)Marshal.OffsetOf<spa_dict_item>(nameof(spa_dict_item.value)));
    }

    [TestMethod]
    public void SpaHook_EmbedsItsListNodeFirst()
    {
        // spa_hook_remove is reimplemented in managed code because the C version is a static inline
        // that exports no symbol. It walks hook->link, so the link has to be where C thinks it is.
        Assert.AreEqual(0, (int)Marshal.OffsetOf<spa_hook>(nameof(spa_hook.link)));
        Assert.AreEqual(2 * sizeof(nint), sizeof(spa_list));
    }

    [TestMethod]
    public void AsyncResultEncoding_MatchesWhatTheDaemonReturns()
    {
        // Not a struct, but the same class of assumption: bit 30 marks a queued request and the
        // low bits carry its sequence. Every round-trip correlates on this.
        Assert.IsTrue(Native.SPA_RESULT_IS_ASYNC(Native.SPA_ASYNC_BIT | 7));
        Assert.AreEqual(7, Native.SPA_RESULT_ASYNC_SEQ(Native.SPA_ASYNC_BIT | 7));

        Assert.IsFalse(Native.SPA_RESULT_IS_ASYNC(0));
        Assert.IsFalse(Native.SPA_RESULT_IS_ASYNC(-13));
    }
}
