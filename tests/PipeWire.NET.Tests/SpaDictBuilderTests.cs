using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// The property-dictionary builder every object creation goes through.
/// </summary>
/// <remarks>
/// It writes into buffers the caller owns and hands the daemon raw pointers into them, so its two
/// failure modes are silent and severe: running past the end of a buffer, and producing a dictionary
/// whose pointers outlive what they point at. Both are guarded, and these check the guards actually
/// fire rather than trusting that they would.
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed unsafe class SpaDictBuilderTests
{
    /// <summary>Reads a built dictionary back the way the parser does, so both agree.</summary>
    private static string? Read(ref spa_dict dict, ReadOnlySpan<byte> key)
    {
        fixed (spa_dict* d = &dict)
        {
            return PipeWireGlobalParser.TryReadValue(d, key, out ReadOnlySpan<byte> value)
                ? Encoding.UTF8.GetString(value)
                : null;
        }
    }

    [TestMethod]
    public void EveryKindOfValue_ComesBackOutAsItWentIn()
    {
        Span<byte> scratch = stackalloc byte[256];
        Span<spa_dict_item> items = stackalloc spa_dict_item[4];
        var builder = new SpaDictBuilder(scratch, items);

        builder.Add("a"u8, "utf8-value"u8);
        builder.Add("b"u8, "managed-value");
        builder.Add("c"u8, 4294967295u);
        builder.Add("d"u8, 0u);

        Assert.AreEqual(4, builder.Count);
        spa_dict dict = builder.Build();

        Assert.AreEqual("utf8-value", Read(ref dict, "a"u8));
        Assert.AreEqual("managed-value", Read(ref dict, "b"u8));
        Assert.AreEqual("4294967295", Read(ref dict, "c"u8), "the widest uint must format in full");
        Assert.AreEqual("0", Read(ref dict, "d"u8));
        Assert.IsNull(Read(ref dict, "missing"u8));
    }

    [TestMethod]
    public void TheFlagsAreZero_BecauseASetSortedBitWouldMakeTheDaemonBinarySearchUnsortedItems()
    {
        Span<byte> scratch = stackalloc byte[64];
        Span<spa_dict_item> items = stackalloc spa_dict_item[1];
        var builder = new SpaDictBuilder(scratch, items);
        builder.Add("k"u8, "v"u8);

        spa_dict dict = builder.Build();

        // SPA_DICT_FLAG_SORTED is bit zero. Set by accident, PipeWire may binary-search items that
        // were added in insertion order and simply not find properties that are there.
        Assert.AreEqual(0u, dict.flags);
        Assert.AreEqual(1u, dict.n_items);
    }

    [TestMethod]
    public void MoreItemsThanTheBufferHolds_IsRefusedRatherThanWrittenPastTheEnd()
    {
        // The item array is the caller's, and overrunning it corrupts whatever is next on the stack.
        Assert.ThrowsExactly<InvalidOperationException>(static () =>
        {
            Span<byte> scratch = stackalloc byte[256];
            Span<spa_dict_item> items = stackalloc spa_dict_item[2];
            var builder = new SpaDictBuilder(scratch, items);

            builder.Add("a"u8, "1"u8);
            builder.Add("b"u8, "2"u8);
            builder.Add("c"u8, "3"u8);
        });
    }

    [TestMethod]
    public void MoreBytesThanTheScratchHolds_IsRefusedRatherThanWrittenPastTheEnd()
    {
        Assert.ThrowsExactly<InvalidOperationException>(static () =>
        {
            Span<byte> scratch = stackalloc byte[8];
            Span<spa_dict_item> items = stackalloc spa_dict_item[4];
            var builder = new SpaDictBuilder(scratch, items);

            builder.Add("a-fairly-long-key"u8, "and-a-fairly-long-value"u8);
        });
    }

    [TestMethod]
    public void AScratchBufferExactlyBigEnough_IsAccepted()
    {
        // The boundary the guard sits on, from the other side: key and value plus one NUL each.
        Span<byte> scratch = stackalloc byte[("ab"u8.Length + 1) + ("cde"u8.Length + 1)];
        Span<spa_dict_item> items = stackalloc spa_dict_item[1];
        var builder = new SpaDictBuilder(scratch, items);

        builder.Add("ab"u8, "cde"u8);
        spa_dict dict = builder.Build();

        Assert.AreEqual("cde", Read(ref dict, "ab"u8));
    }

    [TestMethod]
    public void AnEmptyValue_IsStillAValueRatherThanAnAbsentProperty()
    {
        // PipeWire distinguishes a property set to the empty string from one that is not there, and
        // some of its own keys are used exactly that way.
        Span<byte> scratch = stackalloc byte[64];
        Span<spa_dict_item> items = stackalloc spa_dict_item[1];
        var builder = new SpaDictBuilder(scratch, items);

        builder.Add("empty"u8, ""u8);
        spa_dict dict = builder.Build();

        Assert.AreEqual(string.Empty, Read(ref dict, "empty"u8),
            "the key must be present with an empty value, not absent");
    }

    [TestMethod]
    public void AnEmptyDictionary_IsWellFormed()
    {
        Span<byte> scratch = stackalloc byte[8];
        Span<spa_dict_item> items = stackalloc spa_dict_item[1];
        var builder = new SpaDictBuilder(scratch, items);

        spa_dict dict = builder.Build();

        Assert.AreEqual(0u, dict.n_items);
        Assert.AreEqual(0, builder.Count);
        Assert.IsNull(Read(ref dict, "anything"u8));
    }

    [TestMethod]
    public void ValuesAreNulTerminated_BecauseTheDaemonReadsThemAsCStrings()
    {
        // The daemon reads to the first NUL. Without one it reads into the next item's bytes, which
        // is a property whose value is silently the rest of the buffer.
        Span<byte> scratch = stackalloc byte[64];
        Span<spa_dict_item> items = stackalloc spa_dict_item[2];
        var builder = new SpaDictBuilder(scratch, items);

        builder.Add("first"u8, "one"u8);
        builder.Add("second"u8, "two"u8);

        spa_dict dict = builder.Build();

        Assert.AreEqual("one", Read(ref dict, "first"u8),
            "the first value must stop at its own terminator, not run into the next key");
        Assert.AreEqual("two", Read(ref dict, "second"u8));
    }

    [TestMethod]
    public void NonAsciiValues_KeepTheirBytes()
    {
        Span<byte> scratch = stackalloc byte[128];
        Span<spa_dict_item> items = stackalloc spa_dict_item[1];
        var builder = new SpaDictBuilder(scratch, items);

        const string name = "Gerät – Ünïcödé";
        builder.Add("node.description"u8, name);

        spa_dict dict = builder.Build();
        Assert.AreEqual(name, Read(ref dict, "node.description"u8));
    }
}
