using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Interop;
using PipeWire.NET.Media;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// The boundaries where a value the daemon or a caller chose reaches something that trusts it.
/// </summary>
/// <remarks>
/// No daemon: each case builds the hostile shape directly, which is the only way to reach most of
/// them. A real daemon does not send an unterminated string or a buffer claiming four billion
/// metadata entries, and that is precisely why those paths are never otherwise exercised.
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed unsafe class HostileInputTests
{
    // - Daemon strings -

    [TestMethod]
    public void AStringWithNoTerminator_StopsAtTheCapRatherThanScanningOn()
    {
        // The scan has no other stopping condition, so without the cap it walks off the allocation
        // until a zero byte turns up somewhere in unrelated memory.
        nuint length = DaemonText.MaxBytes + 1024;
        var raw = (byte*)NativeMemory.Alloc(length);
        try
        {
            NativeMemory.Fill(raw, length, 0x41);   // 'A', no NUL anywhere

            ReadOnlySpan<byte> bytes = DaemonText.Bytes((sbyte*)raw);
            Assert.AreEqual(DaemonText.MaxBytes, bytes.Length);

            string text = DaemonText.String((sbyte*)raw)!;
            Assert.AreEqual(DaemonText.MaxBytes, text.Length);
        }
        finally
        {
            NativeMemory.Free(raw);
        }
    }

    [TestMethod]
    public void ATerminatedString_IsReadWholeAndNullIsNull()
    {
        // The other side of the cap, so it cannot pass by truncating everything.
        byte[] utf8 = Encoding.UTF8.GetBytes("alsa_output.pci-0000_00_1f.3.analog-stereo\0");
        fixed (byte* p = utf8)
        {
            Assert.AreEqual(utf8.Length - 1, DaemonText.Bytes((sbyte*)p).Length);
            Assert.AreEqual("alsa_output.pci-0000_00_1f.3.analog-stereo", DaemonText.String((sbyte*)p));
        }

        Assert.IsTrue(DaemonText.Bytes(null).IsEmpty);
        Assert.IsNull(DaemonText.String(null));
    }

    // - Buffer metadata -

    [TestMethod]
    public void ABufferClaimingMoreMetasThanExist_IsWalkedNoFurtherThanTheCap()
    {
        // n_metas belongs to the pool, not to this library, so walking it is walking a number
        // somebody else chose. The cap bounds how far a wrong one reaches; it cannot make the read
        // correct, because nothing in the struct says how long the array really is.
        //
        // Laid out so the cap is observable: a header sits one past it, and finding it would mean
        // the walk went further than the cap allows.
        const uint cap = 64;
        const uint entries = cap + 1;

        var header = new spa_meta_header { pts = 999 };
        var buffer = new spa_buffer
        {
            n_metas = uint.MaxValue,
            metas = (spa_meta*)NativeMemory.AllocZeroed(entries, (nuint)sizeof(spa_meta)),
        };

        try
        {
            for (uint i = 0; i < cap; i++)
                buffer.metas[i].type = (uint)SpaMetaType.Cursor;

            buffer.metas[cap].type = (uint)SpaMetaType.Header;
            buffer.metas[cap].size = (uint)sizeof(spa_meta_header);
            buffer.metas[cap].data = &header;

            Assert.AreEqual(-1, SpaFormatPod.FindPresentationTimeNs(&buffer),
                "the walk read past the entry the cap should have stopped it at");
        }
        finally
        {
            NativeMemory.Free(buffer.metas);
        }
    }

    [TestMethod]
    public void AHeaderMetaWithinTheCap_IsStillFound()
    {
        var header = new spa_meta_header { pts = 1234567 };
        var buffer = new spa_buffer
        {
            n_metas = 1,
            metas = (spa_meta*)NativeMemory.AllocZeroed(1, (nuint)sizeof(spa_meta)),
        };

        try
        {
            buffer.metas[0].type = (uint)SpaMetaType.Header;
            buffer.metas[0].size = (uint)sizeof(spa_meta_header);
            buffer.metas[0].data = &header;

            Assert.AreEqual(1234567, SpaFormatPod.FindPresentationTimeNs(&buffer));
        }
        finally
        {
            NativeMemory.Free(buffer.metas);
        }
    }

    [TestMethod]
    public void AHeaderMetaShorterThanItsStruct_IsIgnoredRatherThanRead()
    {
        var buffer = new spa_buffer
        {
            n_metas = 1,
            metas = (spa_meta*)NativeMemory.AllocZeroed(1, (nuint)sizeof(spa_meta)),
        };

        try
        {
            byte one = 0;
            buffer.metas[0].type = (uint)SpaMetaType.Header;
            buffer.metas[0].size = 1;
            buffer.metas[0].data = &one;

            Assert.AreEqual(-1, SpaFormatPod.FindPresentationTimeNs(&buffer));
        }
        finally
        {
            NativeMemory.Free(buffer.metas);
        }
    }

    // - Creation properties -

    [TestMethod]
    public void TheKeysThatSayWhatAnObjectIs_AreRefusedAsProperties()
    {
        // The caller's properties are written into the dict ahead of the library's own and
        // spa_dict_lookup returns the first match, so accepting these would let a caller route a
        // link somewhere else or hand the request to a different factory.
        Assert.ThrowsExactly<ArgumentException>(
            () => PipeWireObjectOptions.ThrowIfReserved("factory.name", forLink: false));
        Assert.ThrowsExactly<ArgumentException>(
            () => PipeWireObjectOptions.ThrowIfReserved("factory.name", forLink: true));

        foreach (string endpoint in (string[])
                 ["link.output.node", "link.output.port", "link.input.node", "link.input.port"])
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => PipeWireObjectOptions.ThrowIfReserved(endpoint, forLink: true));

            // Not reserved on a node: the key means nothing there, and refusing it would be
            // refusing a property the daemon would simply ignore.
            PipeWireObjectOptions.ThrowIfReserved(endpoint, forLink: false);
        }
    }

    [TestMethod]
    public void TheKeysACallerIsMeantToSet_AreStillAccepted()
    {
        foreach (string key in (string[])
                 ["media.class", "audio.position", "node.name", "object.linger", "link.passive"])
        {
            PipeWireObjectOptions.ThrowIfReserved(key, forLink: false);
            PipeWireObjectOptions.ThrowIfReserved(key, forLink: true);
        }
    }

    [TestMethod]
    public void APropertyCarryingANul_IsRefusedOnBothPaths()
    {
        // Native reads the dictionary as C strings, so a NUL inside a value truncates it there
        // while the managed side believes it sent the whole thing. Both entry points enforce it.
        Assert.ThrowsExactly<ArgumentException>(AddStringWithANul);
        Assert.ThrowsExactly<ArgumentException>(AddSpanWithANul);

        static void AddStringWithANul()
        {
            Span<byte> scratch = new byte[128];
            Span<spa_dict_item> items = new spa_dict_item[4];
            var b = new SpaDictBuilder(scratch, items);
            b.Add("key", "before\0after");
        }

        static void AddSpanWithANul()
        {
            Span<byte> scratch = new byte[128];
            Span<spa_dict_item> items = new spa_dict_item[4];
            var b = new SpaDictBuilder(scratch, items);
            b.Add("media.class"u8, "before\0after"u8);
        }
    }

    // - Profiler reports -

    [TestMethod]
    public void AWellFormedProfilerReport_ParsesToItsObject()
    {
        var expected = new SpaObject(SpaType.ObjectProfiler, SpaParamType.Props,
            [new SpaProperty(1, 0, new SpaInt(7))]);
        byte[] pod = SpaPod.ToBytes(expected);

        fixed (byte* p = pod)
        {
            Assert.IsTrue(PipeWireProfilerReader.TryParseReport(
                (spa_pod*)p, out SpaObject? report, out int size));
            Assert.AreEqual(pod.Length, size);
            Assert.AreEqual(expected, report);
        }
    }

    [TestMethod]
    public void AMalformedProfilerReport_IsRefusedRatherThanSpanned()
    {
        // Null names no size at all.
        Assert.IsFalse(PipeWireProfilerReader.TryParseReport(null, out _, out int nullSize));
        Assert.AreEqual(0, nullSize);

        // An unknown pod type with a buffer-consistent size: well-framed but not a report.
        // The size must fit the buffer it comes in, which the protocol layer guarantees for
        // real traffic: this entry point trusts the message framing and only refuses the
        // content, so a test that lies about the framing would overread by construction.
        byte[] unknown = new byte[16];
        BitConverter.TryWriteBytes(unknown.AsSpan(), 8u);
        BitConverter.TryWriteBytes(unknown.AsSpan(4), 0xDEADBEEFu);
        fixed (byte* u = unknown)
            Assert.IsFalse(PipeWireProfilerReader.TryParseReport((spa_pod*)u, out _, out _));

        // A size near uint.MaxValue must refuse before spanning: only the header is read.
        byte[] huge = new byte[8];
        BitConverter.TryWriteBytes(huge.AsSpan(), 0xFFFFFF00u);
        BitConverter.TryWriteBytes(huge.AsSpan(4), (uint)SpaType.Object);
        fixed (byte* h = huge)
        {
            Assert.IsFalse(
                PipeWireProfilerReader.TryParseReport((spa_pod*)h, out _, out int size));
            Assert.AreEqual(int.MaxValue, size);
        }

        // Well-formed but not a report.
        byte[] integer = new byte[16];
        BitConverter.TryWriteBytes(integer.AsSpan(), 4u);
        BitConverter.TryWriteBytes(integer.AsSpan(4), (uint)SpaType.Int);
        BitConverter.TryWriteBytes(integer.AsSpan(8), 42);
        fixed (byte* i = integer)
            Assert.IsFalse(PipeWireProfilerReader.TryParseReport((spa_pod*)i, out _, out _));
    }

    // - Errors -

    [TestMethod]
    public void ADaemonRefusal_IsNotAnInvalidOperationException()
    {
        // Sharing a base with InvalidOperationException means every catch written to contain a
        // local state bug also swallows a permission refusal or a dropped connection, silently and
        // in code that never mentioned PipeWire.
        var e = new PipeWireException("update_permissions", -13);

        Assert.IsNotInstanceOfType<InvalidOperationException>(e);
        Assert.IsInstanceOfType<Exception>(e);
    }

    [TestMethod]
    public void TheCodesTheDaemonReturns_AreNamedInTheMessage()
    {
        // The number is what a caller branches on, but the message is read by a person, and the
        // symbolic name is the half of it that says what went wrong. Every mapped code is named;
        // anything else still reports the number rather than inventing a name for it.
        foreach ((int code, string name) in new[]
        {
            (-1, "EPERM"), (-2, "ENOENT"), (-4, "EINTR"), (-5, "EIO"), (-9, "EBADF"),
            (-11, "EAGAIN"), (-12, "ENOMEM"), (-13, "EACCES"), (-14, "EFAULT"), (-16, "EBUSY"),
            (-17, "EEXIST"), (-19, "ENODEV"), (-22, "EINVAL"), (-24, "EMFILE"), (-25, "ENOTTY"),
            (-28, "ENOSPC"), (-32, "EPIPE"), (-34, "ERANGE"), (-38, "ENOSYS"), (-39, "ENOTEMPTY"),
            (-71, "EPROTO"), (-74, "EBADMSG"), (-75, "EOVERFLOW"), (-84, "EILSEQ"),
            (-88, "ENOTSOCK"), (-90, "EMSGSIZE"), (-93, "EPROTONOSUPPORT"), (-95, "EOPNOTSUPP"),
            (-98, "EADDRINUSE"), (-103, "ECONNABORTED"), (-104, "ECONNRESET"), (-105, "ENOBUFS"),
            (-107, "ENOTCONN"), (-108, "ESHUTDOWN"), (-110, "ETIMEDOUT"), (-111, "ECONNREFUSED"),
            (-125, "ECANCELED"),
        })
        {
            StringAssert.Contains(new PipeWireException("op", code).Message, name,
                $"{code} is not named in the message");
        }

        string unmapped = new PipeWireException("op", -9999).Message;
        StringAssert.Contains(unmapped, "-9999");
        Assert.IsFalse(unmapped.Contains('(', StringComparison.Ordinal),
            "an unmapped code was given a symbolic name");
    }

    [TestMethod]
    public void TheMessageOnlyShapes_CarryNoCode()
    {
        // The standard exception shapes exist for throw sites with no daemon result to report.
        // Zero then means no code was available, not success.
        var bare = new PipeWireException();
        Assert.AreEqual("unknown", bare.Operation);
        Assert.AreEqual(0, bare.Result);

        var noted = new PipeWireException("custom");
        Assert.AreEqual("custom", noted.Message);
        Assert.AreEqual(0, noted.Result);

        var inner = new PipeWireException("custom", new InvalidOperationException());
        Assert.IsInstanceOfType<InvalidOperationException>(inner.InnerException);
    }

    [TestMethod]
    public void ThrowIfFailed_ThrowsOnlyOnFailure()
    {
        PipeWireException.ThrowIfFailed(0, "op");
        PipeWireException.ThrowIfFailed(3, "op");

        PipeWireException thrown = Assert.ThrowsExactly<PipeWireException>(
            () => PipeWireException.ThrowIfFailed(-2, "op", 7));
        Assert.AreEqual(-2, thrown.Result);
        Assert.AreEqual("op", thrown.Operation);
        Assert.AreEqual((uint)7, thrown.ObjectId);
    }
    [TestMethod]
    public void BothRefusalCodes_ReadAsPermissionDenied()
    {
        // EACCES is a permission bit the client does not hold and EPERM is an operation it may not
        // perform at all. A caller asking "was I allowed" wants the same answer for each.
        Assert.IsTrue(new PipeWireException("update_permissions", -13).IsPermissionDenied);
        Assert.IsTrue(new PipeWireException("update_permissions", -1).IsPermissionDenied);

        Assert.IsFalse(new PipeWireException("create", -22).IsPermissionDenied);
        Assert.IsFalse(new PipeWireException("create", -22).IsDisconnected);
        Assert.IsTrue(new PipeWireException("write", -32).IsDisconnected);
    }

    [TestMethod]
    public void ADaemonFailureCarriesItsCode_RatherThanASentence()
    {
        // The code is the part a caller branches on; the message is for a person reading a log.
        var error = new PipeWireException("pw_context_connect", -2, null, "ensure the daemon is running");

        Assert.AreEqual(-2, error.Result);
        Assert.AreEqual("pw_context_connect", error.Operation);
        Assert.IsTrue(error.Message.Contains("ENOENT", StringComparison.Ordinal));
        Assert.IsTrue(error.Message.Contains("ensure the daemon is running", StringComparison.Ordinal));
    }

    // - Interface dispatch -

    [TestMethod]
    public void DispatchAgainstAnObjectWithNoMethods_RefusesRatherThanCrashing()
    {
        // Every dispatch wrapper reads the method table out of the object and must refuse when
        // it is not there. A zeroed stand-in has no table at all, which is the most hostile
        // shape: dereferencing it would be a null call through a function pointer.
        spa_interface fake = default;
        spa_interface* obj = &fake;

        Assert.AreEqual(-1, Native.pw_registry_add_listener((pw_registry*)obj, null, null, null));
        Assert.AreEqual(-1, Native.pw_registry_destroy_global((pw_registry*)obj, 0));
        Assert.AreEqual(-1, Native.pw_core_sync((pw_core*)obj, 0, 0));
        Assert.AreEqual(-1, Native.pw_core_add_listener((pw_core*)obj, null, null, null));
        Assert.IsTrue(Native.pw_registry_bind((pw_registry*)obj, 0, null, 0, 0) is null);
        Assert.AreEqual(-1, Native.pw_node_add_listener((pw_node*)obj, null, null, null));
        Assert.AreEqual(-1, Native.pw_node_enum_params((pw_node*)obj, 0, 0, 0, 0, null));
        Assert.AreEqual(-1, Native.pw_node_set_param((pw_node*)obj, 0, 0, null));
        Assert.AreEqual(-1, Native.pw_node_subscribe_params((pw_node*)obj, null, 0));
        Assert.AreEqual(-1, Native.pw_device_add_listener((pw_device*)obj, null, null, null));
        Assert.AreEqual(-1, Native.pw_device_enum_params((pw_device*)obj, 0, 0, 0, 0, null));
        Assert.AreEqual(-1, Native.pw_device_set_param((pw_device*)obj, 0, 0, null));
        Assert.AreEqual(-1, Native.pw_device_subscribe_params((pw_device*)obj, null, 0));
        Assert.AreEqual(-1, Native.pw_port_add_listener((pw_port*)obj, null, null, null));
        Assert.AreEqual(-1, Native.pw_port_enum_params((pw_port*)obj, 0, 0, 0, 0, null));
        Assert.AreEqual(-1, Native.pw_port_subscribe_params((pw_port*)obj, null, 0));
        Assert.AreEqual(-1, Native.pw_link_add_listener((pw_link*)obj, null, null, null));
        Assert.AreEqual(-1, Native.pw_client_update_permissions((pw_client*)obj, 0, null));
        Assert.AreEqual(-1, Native.pw_client_get_permissions((pw_client*)obj, 0, 0));
        Assert.AreEqual(-1, Native.pw_client_update_properties((pw_client*)obj, null));
        Assert.AreEqual(-1, Native.pw_profiler_add_listener(obj, null, null, null));
        Assert.AreEqual(-1, Native.pw_security_context_create(obj, 0, 0, null));
        Assert.AreEqual(-1, Native.pw_metadata_add_listener((pw_metadata*)obj, null, null, null));
        Assert.AreEqual(-1, Native.pw_metadata_set_property((pw_metadata*)obj, 0, null, null, null));
        Assert.AreEqual(-1, Native.pw_metadata_clear((pw_metadata*)obj));

        // Creating is not reportable as -1: there is no call to fail, so it throws ENOSYS.
        PipeWireException refused = Assert.ThrowsExactly<PipeWireException>(
            () => Native.pw_core_get_registry((pw_core*)obj, 0, 0));
        Assert.AreEqual(-38, refused.Result);
        refused = Assert.ThrowsExactly<PipeWireException>(
            () => Native.pw_core_create_object((pw_core*)obj, null, null, 0, null, 0));
        Assert.AreEqual(-38, refused.Result);

        // Detaching nothing detaches nothing.
        Native.spa_hook_remove(null);
    }
}
