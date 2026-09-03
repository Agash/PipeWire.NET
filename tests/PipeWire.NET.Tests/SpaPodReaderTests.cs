using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// <see cref="SpaPodReader"/> parses buffers the daemon sends, so it has to survive buffers that are
/// truncated, mistyped or the wrong size without reading past its span. These are the inputs a
/// well-behaved daemon never sends and a broken or hostile one does.
/// </summary>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class SpaPodReaderTests
{
    // --- pod construction helpers -------------------------------------------------------

    /// <summary>A pod is [uint32 size][uint32 type][body, padded to 8].</summary>
    private static byte[] Pod(SpaType type, ReadOnlySpan<byte> body)
    {
        int padded = (body.Length + 7) & ~7;
        var pod = new byte[8 + padded];
        BitConverter.TryWriteBytes(pod.AsSpan(0, 4), (uint)body.Length);
        BitConverter.TryWriteBytes(pod.AsSpan(4, 4), (uint)type);
        body.CopyTo(pod.AsSpan(8));
        return pod;
    }

    private static byte[] IntPod(int v) => Pod(SpaType.Int, BitConverter.GetBytes(v));
    private static byte[] LongPod(long v) => Pod(SpaType.Long, BitConverter.GetBytes(v));
    private static byte[] FloatPod(float v) => Pod(SpaType.Float, BitConverter.GetBytes(v));
    private static byte[] DoublePod(double v) => Pod(SpaType.Double, BitConverter.GetBytes(v));
    private static byte[] BoolPod(bool v) => Pod(SpaType.Bool, BitConverter.GetBytes(v ? 1 : 0));
    private static byte[] IdPod(uint v) => Pod(SpaType.Id, BitConverter.GetBytes(v));

    private static byte[] RectanglePod(uint w, uint h)
    {
        Span<byte> body = stackalloc byte[8];
        BitConverter.TryWriteBytes(body[..4], w);
        BitConverter.TryWriteBytes(body[4..], h);
        return Pod(SpaType.Rectangle, body);
    }

    private static byte[] FractionPod(uint num, uint den)
    {
        Span<byte> body = stackalloc byte[8];
        BitConverter.TryWriteBytes(body[..4], num);
        BitConverter.TryWriteBytes(body[4..], den);
        return Pod(SpaType.Fraction, body);
    }

    // --- primitives round-trip ----------------------------------------------------------

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(-1)]
    [DataRow(int.MaxValue)]
    [DataRow(int.MinValue)]
    public void ReadInt_RoundTripsBoundaryValues(int value)
    {
        var r = new SpaPodReader(IntPod(value));
        Assert.AreEqual(value, r.ReadInt());
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(long.MaxValue)]
    [DataRow(long.MinValue)]
    public void ReadLong_RoundTripsBoundaryValues(long value)
    {
        var r = new SpaPodReader(LongPod(value));
        Assert.AreEqual(value, r.ReadLong());
    }

    [TestMethod]
    public void ReadFloatAndDouble_RoundTripSpecialValues()
    {
        Assert.AreEqual(0f, new SpaPodReader(FloatPod(0f)).ReadFloat());
        Assert.AreEqual(float.MaxValue, new SpaPodReader(FloatPod(float.MaxValue)).ReadFloat());
        Assert.IsTrue(float.IsNaN(new SpaPodReader(FloatPod(float.NaN)).ReadFloat()));
        Assert.IsTrue(double.IsNegativeInfinity(
            new SpaPodReader(DoublePod(double.NegativeInfinity)).ReadDouble()));
    }

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(1, true)]
    [DataRow(2, true)]      // SPA writes 0/1, but anything non-zero must not read as false
    [DataRow(-1, true)]
    public void ReadBool_TreatsAnyNonZeroAsTrue(int raw, bool expected)
    {
        var r = new SpaPodReader(Pod(SpaType.Bool, BitConverter.GetBytes(raw)));
        Assert.AreEqual(expected, r.ReadBool());
    }

    [TestMethod]
    public void ReadRectangleAndFraction_ReadBothHalvesInOrder()
    {
        Assert.AreEqual((1920u, 1080u), new SpaPodReader(RectanglePod(1920, 1080)).ReadRectangle());
        Assert.AreEqual((30u, 1u), new SpaPodReader(FractionPod(30, 1)).ReadFraction());

        // Asymmetric values catch a swapped pair, which equal values would hide.
        Assert.AreEqual((7u, 13u), new SpaPodReader(RectanglePod(7, 13)).ReadRectangle());
    }

    [TestMethod]
    public void ReadId_AcceptsTheFullUnsignedRange()
    {
        Assert.AreEqual(0u, new SpaPodReader(IdPod(0)).ReadId());
        Assert.AreEqual(uint.MaxValue, new SpaPodReader(IdPod(uint.MaxValue)).ReadId());
    }

    // --- wrong type and wrong size ------------------------------------------------------

    [TestMethod]
    public void ReadingTheWrongType_Throws()
    {
        // A Long pod read as an Int would otherwise silently return the low four bytes.
        Assert.ThrowsExactly<InvalidOperationException>(() => new SpaPodReader(LongPod(1)).ReadInt());
        Assert.ThrowsExactly<InvalidOperationException>(() => new SpaPodReader(IntPod(1)).ReadLong());
        Assert.ThrowsExactly<InvalidOperationException>(() => new SpaPodReader(IntPod(1)).ReadFloat());
        Assert.ThrowsExactly<InvalidOperationException>(() => new SpaPodReader(IntPod(1)).ReadRectangle());
    }

    [TestMethod]
    public void ReadingAPodWhoseSizeDisagreesWithItsType_Throws()
    {
        // Claims Int but declares a 2-byte body: a daemon bug or a corrupted buffer.
        byte[] pod = Pod(SpaType.Int, new byte[2]);
        Assert.ThrowsExactly<InvalidOperationException>(() => new SpaPodReader(pod).ReadInt());
    }

    // --- truncation ---------------------------------------------------------------------

    [TestMethod]
    public void ATruncatedHeader_IsRejectedRatherThanReadPastTheBuffer()
    {
        for (int len = 0; len < 8; len++)
        {
            var reader = new SpaPodReader(new byte[len]);
            Assert.IsFalse(reader.EnterObject(out _, out _, out _),
                $"a {len}-byte buffer cannot contain a pod header");
        }
    }

    [TestMethod]
    public void APodHeaderPromisingMoreBodyThanExists_IsRejected()
    {
        var pod = new byte[16];
        BitConverter.TryWriteBytes(pod.AsSpan(0, 4), 64u);
        BitConverter.TryWriteBytes(pod.AsSpan(4, 4), (uint)SpaType.Object);

        var reader = new SpaPodReader(pod);
        Assert.IsFalse(reader.EnterObject(out _, out _, out _),
            "a pod declaring more body than the buffer holds must be refused, not reported");
    }

    [TestMethod]
    [DataRow(0u)]
    [DataRow(1u)]
    [DataRow(7u)]
    public void AnObjectPodDeclaringLessThanItsOwnHeader_IsRejected(uint declaredSize)
    {
        // bodySize is computed as size - 8, so anything under 8 underflows to nearly 4GB.
        var pod = new byte[24];
        BitConverter.TryWriteBytes(pod.AsSpan(0, 4), declaredSize);
        BitConverter.TryWriteBytes(pod.AsSpan(4, 4), (uint)SpaType.Object);

        var reader = new SpaPodReader(pod);
        Assert.IsFalse(reader.EnterObject(out _, out _, out uint bodySize),
            $"an object declaring {declaredSize} bytes cannot hold its own type and id");
        Assert.AreEqual(0u, bodySize, "a refused read must not report a body size");
    }

    [TestMethod]
    public void EnterObject_RejectsAPodThatIsNotAnObject()
    {
        Assert.IsFalse(new SpaPodReader(IntPod(1)).EnterObject(out _, out _, out _));
        Assert.IsFalse(new SpaPodReader(LongPod(1)).EnterObject(out _, out _, out _));
    }

    // --- property iteration -------------------------------------------------------------

    [TestMethod]
    public void TryReadProperty_WalksEveryPropertyThenStops()
    {
        byte[] body = BuildObjectBody(
            (key: 1u, flags: 0u, value: IntPod(42)),
            (key: 2u, flags: 0u, value: LongPod(7)),
            (key: 3u, flags: 0u, value: RectanglePod(640, 480)));

        var reader = new SpaPodReader(body);
        Assert.IsTrue(reader.EnterObject(out _, out _, out _));

        Assert.IsTrue(reader.TryReadProperty(out SpaKey k1, out SpaPodReader v1));
        Assert.AreEqual(1u, k1.Value);
        Assert.AreEqual(42, v1.ReadInt());

        Assert.IsTrue(reader.TryReadProperty(out SpaKey k2, out SpaPodReader v2));
        Assert.AreEqual(2u, k2.Value);
        Assert.AreEqual(7L, v2.ReadLong());

        Assert.IsTrue(reader.TryReadProperty(out SpaKey k3, out SpaPodReader v3));
        Assert.AreEqual(3u, k3.Value);
        Assert.AreEqual((640u, 480u), v3.ReadRectangle());

        Assert.IsFalse(reader.TryReadProperty(out _, out _), "the object had exactly three properties");
    }

    [TestMethod]
    public void TryReadProperty_SurfacesTheFlagsThatGateFixation()
    {
        byte[] body = BuildObjectBody((key: 9u, flags: SpaPodPropFlag.DontFixate, value: IntPod(1)));

        var reader = new SpaPodReader(body);
        Assert.IsTrue(reader.EnterObject(out _, out _, out _));
        Assert.IsTrue(reader.TryReadProperty(out SpaKey key, out uint flags, out _));
        Assert.AreEqual(9u, key.Value);
        Assert.AreEqual(SpaPodPropFlag.DontFixate, flags & SpaPodPropFlag.DontFixate,
            "DontFixate must survive; it is what says a modifier list is still a choice");
    }

    [TestMethod]
    public void TryReadProperty_StopsOnATruncatedPropertyRatherThanOverrunning()
    {
        byte[] full = BuildObjectBody((key: 1u, flags: 0u, value: IntPod(42)));

        for (int cut = 8; cut < full.Length; cut++)
        {
            var reader = new SpaPodReader(full.AsSpan(0, cut).ToArray());
            if (!reader.EnterObject(out _, out _, out _)) continue;

            // Whatever it decides, it must not throw an out-of-range: it either reads or declines.
            try { _ = reader.TryReadProperty(out _, out _); }
            catch (InvalidOperationException) { /* a declared type/size mismatch is a fair refusal */ }
        }
    }

    [TestMethod]
    public void PositionAndIsAtEnd_TrackConsumption()
    {
        var reader = new SpaPodReader(IntPod(5));
        Assert.AreEqual(0, reader.Position);
        Assert.IsFalse(reader.IsAtEnd);

        _ = reader.ReadInt();
        Assert.IsTrue(reader.Position > 0);
    }

    [TestMethod]
    public void AnEmptyBuffer_IsAtEndAndReadsNothing()
    {
        var reader = new SpaPodReader(ReadOnlySpan<byte>.Empty);
        Assert.AreEqual(0, reader.Length);
        Assert.IsTrue(reader.IsAtEnd);
        Assert.IsFalse(reader.EnterObject(out _, out _, out _));
    }

    // --- helper -------------------------------------------------------------------------

    /// <summary>Wraps properties in an object pod, matching the layout the daemon emits.</summary>
    private static byte[] BuildObjectBody(params (uint key, uint flags, byte[] value)[] props)
    {
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes(0u));   // object type
        body.AddRange(BitConverter.GetBytes(0u));   // object id
        foreach ((uint key, uint flags, byte[] value) in props)
        {
            body.AddRange(BitConverter.GetBytes(key));
            body.AddRange(BitConverter.GetBytes(flags));
            body.AddRange(value);
            while (body.Count % 8 != 0) body.Add(0);
        }
        return Pod(SpaType.Object, CollectionsMarshal.AsSpan(body));
    }
}
