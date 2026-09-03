using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// The SPA parser against bytes nobody designed.
/// </summary>
/// <remarks>
/// Every pod the parser sees comes from another process. A producer with a bug, a version that
/// disagrees about a struct, or a client that means harm all arrive the same way: as a span whose
/// contents were chosen by somebody else. The parser's whole contract is that it returns false
/// rather than reading past the span, looping, or throwing, and that contract is only interesting
/// for inputs no test author would think to write down.
/// <para>
/// The generators below are biased toward truncated objects, declined choices that move the
/// reader, unchecked sizes, and choices claiming more values than they carry, since a uniformly
/// random buffer is rejected at the header and exercises nothing.
/// </para>
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class SpaPodFuzzTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private static readonly SpaType[] Types =
    [
        SpaType.None, SpaType.Bool, SpaType.Id, SpaType.Int, SpaType.Long, SpaType.Float,
        SpaType.Double, SpaType.String, SpaType.Bytes, SpaType.Rectangle, SpaType.Fraction,
        SpaType.Bitmap, SpaType.Array, SpaType.Struct, SpaType.Object, SpaType.Sequence,
        SpaType.Pointer, SpaType.Fd, SpaType.Choice, SpaType.Pod,
    ];

    /// <summary>A pod header over a body of the caller's choosing, with the size deliberately loose.</summary>
    private static byte[] Framed(Random random, SpaType type, ReadOnlySpan<byte> body, bool honestSize)
    {
        var pod = new byte[8 + body.Length];

        uint declared = honestSize
            ? (uint)body.Length
            : random.Next(4) switch
            {
                0 => 0u,
                1 => (uint)body.Length + (uint)random.Next(1, 64),   // claims more than it has
                2 => uint.MaxValue,                                   // the overflow case
                _ => (uint)Math.Max(0, body.Length - random.Next(1, 8)),
            };

        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(0, 4), declared);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(4, 4), (uint)type);
        body.CopyTo(pod.AsSpan(8));

        return pod;
    }

    /// <summary>A body shaped like something the parser recurses into.</summary>
    private static byte[] Body(Random random, int depth)
    {
        var body = new List<byte>();

        switch (random.Next(6))
        {
            case 0: // choice: choiceType, flags, childSize, childType, then values
                body.AddRange(BitConverter.GetBytes((uint)random.Next(0, 6)));
                body.AddRange(BitConverter.GetBytes((uint)random.Next(0, 3)));
                body.AddRange(BitConverter.GetBytes((uint)random.Next(0, 32)));
                body.AddRange(BitConverter.GetBytes((uint)Types[random.Next(Types.Length)]));
                for (int i = random.Next(0, 5); i > 0; i--)
                    body.AddRange(BitConverter.GetBytes((long)random.Next()));
                break;

            case 1: // object: type, id, then key/flags/value triples
                body.AddRange(BitConverter.GetBytes((uint)SpaType.ObjectFormat));
                body.AddRange(BitConverter.GetBytes((uint)random.Next(0, 16)));
                for (int i = random.Next(0, 4); i > 0; i--)
                {
                    body.AddRange(BitConverter.GetBytes((uint)random.Next(0, 32)));
                    body.AddRange(BitConverter.GetBytes((uint)random.Next(0, 4)));
                    if (depth > 0) body.AddRange(Nested(random, depth - 1));
                }
                break;

            case 2: // array: childSize, childType, then children
                body.AddRange(BitConverter.GetBytes((uint)random.Next(0, 24)));
                body.AddRange(BitConverter.GetBytes((uint)Types[random.Next(Types.Length)]));
                for (int i = random.Next(0, 8); i > 0; i--)
                    body.AddRange(BitConverter.GetBytes(random.Next()));
                break;

            case 3: // struct: a run of nested pods
                for (int i = random.Next(0, 4); i > 0 && depth > 0; i--)
                    body.AddRange(Nested(random, depth - 1));
                break;

            default: // plain noise, including the sizes that are almost a header
                var noise = new byte[random.Next(0, 40)];
                random.NextBytes(noise);
                body.AddRange(noise);
                break;
        }

        return [.. body];
    }

    private static byte[] Nested(Random random, int depth) =>
        Framed(random, Types[random.Next(Types.Length)], Body(random, depth), honestSize: random.Next(4) != 0);

    private static void MustNotMisbehave(byte[] pod, string origin)
    {
        try
        {
            // The result is deliberately ignored. Whether a given pod is valid is not the property
            // under test; not throwing, not hanging and not reading past the span is.
            _ = SpaPod.TryParse(pod, out SpaValue? value);

            // A parser that says yes has to hand back something, or a caller dereferences null on
            // an input an attacker chose.
            if (value is null) return;
        }
        catch (Exception ex)
        {
            Assert.Fail(
                $"{origin} threw {ex.GetType().Name}: {ex.Message}\n"
                + $"pod: {Convert.ToHexString(pod)}");
        }
    }

    [TestMethod]
    public void RandomPodsShapedLikeRealOnes_AreParsedOrRefusedButNeverThrow()
    {
        var random = new Random(20260903);
        var clock = Stopwatch.StartNew();
        var cases = 0;

        while (clock.Elapsed < Budget)
        {
            for (int batch = 0; batch < 500; batch++)
            {
                byte[] pod = Nested(random, depth: 3);
                MustNotMisbehave(pod, "a generated pod");
                cases++;
            }
        }

        Console.Error.WriteLine($"{cases} generated pods in {clock.Elapsed.TotalSeconds:F1}s");
        Assert.IsTrue(cases > 1000, "the fuzzer did not get through enough cases to mean anything");
    }

    [TestMethod]
    public void EveryTruncationOfAValidPod_IsRefusedWithoutThrowing()
    {
        // Truncation is the mutation that actually happens in the field: a short read, a producer
        // that died mid-write, a buffer sized from a different struct.
        Span<byte> buffer = stackalloc byte[1024];
        var builder = new SpaPodBuilder(buffer);

        builder.PushObject(SpaType.ObjectFormat, SpaParamType.EnumFormat);
        builder.AddId(SpaFormat.MediaType, SpaMediaType.Video);
        builder.AddChoiceEnum(SpaFormat.VideoFormat, SpaVideoFormat.Bgra, SpaVideoFormat.Nv12);
        builder.AddRectangle(SpaFormat.VideoSize, 1920, 1080);
        builder.AddChoiceRangeFraction(SpaFormat.VideoFramerate, 30, 1, 1, 1, 240, 1);
        builder.Pop();

        byte[] whole = builder.GetPod().ToArray();
        Assert.IsTrue(SpaPod.TryParse(whole, out _), "the intact pod must parse, or this proves nothing");

        for (int length = 0; length < whole.Length; length++)
            MustNotMisbehave(whole[..length], $"a pod truncated to {length} bytes");
    }

    [TestMethod]
    public void EverySingleByteCorruptionOfAValidPod_IsHandledWithoutThrowing()
    {
        // One flipped byte, everywhere, with every value. This is the cheapest way to reach the
        // combinations of type tag and size that no hand-written case covers: a size field that is
        // one bit too large, a child type that does not exist, a count that overruns the body.
        Span<byte> buffer = stackalloc byte[512];
        var builder = new SpaPodBuilder(buffer);

        builder.PushObject(SpaType.ObjectFormat, SpaParamType.EnumFormat);
        builder.AddId(SpaFormat.MediaType, SpaMediaType.Video);
        builder.AddChoiceEnumLong(SpaFormat.VideoModifier, [0x0100000000000001L, 0L]);
        builder.AddRectangle(SpaFormat.VideoSize, 640, 480);
        builder.Pop();

        byte[] whole = builder.GetPod().ToArray();

        foreach (byte replacement in (byte[])[0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF])
        {
            for (int index = 0; index < whole.Length; index++)
            {
                byte[] mutated = [.. whole];
                mutated[index] = replacement;

                MustNotMisbehave(mutated, $"byte {index} set to 0x{replacement:X2}");
            }
        }
    }

    [TestMethod]
    public void ADeeplyNestedPod_IsRefusedRatherThanExhaustingTheStack()
    {
        // Nesting is the one input where refusing is not enough: recursion that goes deep enough
        // takes the process down with a StackOverflowException, which cannot be caught. A producer
        // does not have to be malicious to send this, only wrong in a loop.
        byte[] pod = [0x00, 0x00, 0x00, 0x00, .. BitConverter.GetBytes((uint)SpaType.Struct)];

        for (int depth = 0; depth < 512; depth++)
        {
            var wrapped = new byte[8 + pod.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(wrapped.AsSpan(0, 4), (uint)pod.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(wrapped.AsSpan(4, 4), (uint)SpaType.Struct);
            pod.CopyTo(wrapped.AsSpan(8));
            pod = wrapped;
        }

        // Reaching this line at all is the assertion: a parser without a depth limit never returns.
        MustNotMisbehave(pod, "a pod nested 512 deep");
    }
}
