using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Interop;

namespace PipeWire.NET.Tests;

/// <summary>
/// The generated property-key surface, and the one hand-written step that produces half of it.
/// </summary>
/// <remarks>
/// <para>
/// ClangSharp renders a string macro as a UTF-8 span, which is the form the native side wants. The
/// <see cref="string"/> form callers read properties with has no generator support, so
/// <c>generate/generate.sh</c> derives <see cref="PipeWireKeys"/> from those spans with a script.
/// That script is the only hand-written link in the chain, and nothing else checks it.
/// </para>
/// <para>
/// A drift here does not throw. The two forms simply stop naming the same property, so the library
/// writes a key under one spelling and reads it back under another, and every lookup quietly
/// returns nothing.
/// </para>
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class GeneratedKeysTests
{
    private delegate ReadOnlySpan<byte> SpanGetter();

    /// <summary>Every <c>ReadOnlySpan&lt;byte&gt;</c> constant on <c>NativeConstants</c>, by name.</summary>
    /// <remarks>
    /// A span cannot be boxed, so <see cref="PropertyInfo.GetValue(object)"/> throws on these. The
    /// getter is bound as a delegate and the result copied out instead.
    /// </remarks>
    private static List<(string Name, byte[] Utf8)> NativeSpans() =>
        [
            .. typeof(NativeConstants)
                .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(p => p.PropertyType == typeof(ReadOnlySpan<byte>))
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p =>
                    (
                        p.Name,
                        (
                            (SpanGetter)
                                Delegate.CreateDelegate(typeof(SpanGetter), p.GetGetMethod(true)!)
                        )()
                            .ToArray()
                    )
                ),
        ];

    private static Dictionary<string, string> StringKeys() =>
        typeof(PipeWireKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(
                f => f.Name,
                f => (string)f.GetRawConstantValue()!,
                StringComparer.Ordinal
            );

    /// <summary>
    /// The derived string form and the generated span form name the same property, for every key.
    /// </summary>
    /// <remarks>
    /// This is the assertion the derivation script exists to satisfy. It compares decoded bytes
    /// rather than re-deriving the string, on purpose: re-deriving would run the same
    /// transformation the script ran and agree with itself no matter what that transformation did.
    /// </remarks>
    [TestMethod]
    public void EveryDerivedStringKey_DecodesToItsGeneratedSpan()
    {
        Dictionary<string, string> strings = StringKeys();
        var mismatches = new List<string>();
        var compared = 0;

        foreach ((string name, byte[] utf8) in NativeSpans())
        {
            if (!strings.TryGetValue(name, out string? asString))
                continue;

            compared++;
            string decoded = Encoding.UTF8.GetString(utf8);
            if (!string.Equals(decoded, asString, StringComparison.Ordinal))
                mismatches.Add($"{name}: span='{decoded}' string='{asString}'");
        }

        Assert.AreEqual(0, mismatches.Count, string.Join("; ", mismatches));

        // A derivation that emitted nothing at all would satisfy every assertion above.
        Assert.IsTrue(compared > 250, $"only {compared} keys were cross-checked");
    }

    /// <summary>
    /// A generated span is NUL-terminated past its length, which is what lets it reach C unchanged.
    /// </summary>
    /// <remarks>
    /// The reason these reach <c>spa_dict</c> without transcoding is that a <c>u8</c> literal's
    /// representation carries a NUL beyond its logical length, so a pointer to one is already a
    /// valid C string. Nothing in the type system says so, and a change in how the generator emits
    /// them would break every native call at once, silently, by passing a string that runs on into
    /// whatever follows it.
    /// </remarks>
    [TestMethod]
    public unsafe void AGeneratedSpan_IsNulTerminatedForC()
    {
        fixed (byte* p = NativeConstants.PW_KEY_NODE_NAME)
        {
            Assert.AreEqual(
                (byte)0,
                p[NativeConstants.PW_KEY_NODE_NAME.Length],
                "a u8 literal handed to native code was not NUL-terminated past its length"
            );
        }

        fixed (byte* p = NativeConstants.PW_TYPE_INTERFACE_Node)
        {
            Assert.AreEqual((byte)0, p[NativeConstants.PW_TYPE_INTERFACE_Node.Length]);
        }
    }

    /// <summary>The keys are well formed: a property name with whitespace or a NUL in it is a bug.</summary>
    [TestMethod]
    public void EveryStringKey_IsAPlausiblePropertyName()
    {
        Dictionary<string, string> keys = StringKeys();
        Assert.IsTrue(keys.Count > 250, $"only {keys.Count} keys were generated");

        foreach ((string name, string value) in keys)
        {
            Assert.AreNotEqual(0, value.Length, $"{name} is empty");
            Assert.IsFalse(
                value.Contains('\0', StringComparison.Ordinal),
                $"{name} contains a NUL"
            );
            Assert.IsFalse(value.Any(char.IsWhiteSpace), $"{name} contains whitespace: '{value}'");
        }
    }

    /// <summary>
    /// The two keys this library defines itself are present, and are not emitted as generated ones.
    /// </summary>
    /// <remarks>
    /// They are fields of <c>struct pw_module_info</c>, not macros - no header declares a name for
    /// them, so they live in a hand-written partial. Emitting them from the derivation script put a
    /// library-invented name among upstream's, where it read as upstream's and would vanish on the
    /// next regeneration.
    /// </remarks>
    [TestMethod]
    public void TheLibraryDefinedKeys_AreNotEmittedIntoTheGeneratedFile()
    {
        // Read through reflection rather than compared to a literal: a direct comparison of two
        // constants is folded at compile time and asserts nothing about the shipped assembly.
        Dictionary<string, string> keys = StringKeys();

        Assert.AreEqual(
            "module.filename",
            keys.GetValueOrDefault(nameof(PipeWireKeys.MODULE_FILENAME))
        );
        Assert.AreEqual("module.args", keys.GetValueOrDefault(nameof(PipeWireKeys.MODULE_ARGS)));

        string generated = File.ReadAllText(
            Path.Combine(
                PublicSurfaceTests.RepoRoot(),
                "src",
                "PipeWire.NET",
                "generated",
                "PipeWireKeys.g.cs"
            )
        );

        Assert.IsFalse(
            generated.Contains("MODULE_FILENAME", StringComparison.Ordinal),
            "a library-defined key is being emitted into the generated file, where the next "
                + "regeneration drops it"
        );
    }

    /// <summary>
    /// Where PipeWire and SPA both name a property, they name the same one.
    /// </summary>
    /// <remarks>
    /// <c>PW_KEY_MEDIA_CLASS</c> and <c>SPA_KEY_MEDIA_CLASS</c> are both "media.class", and a call
    /// site uses whichever is in scope. If the two headers diverged, reads and writes would land on
    /// different properties depending on which constant happened to be reached for.
    /// </remarks>
    [TestMethod]
    public void WhereBothNamespacesNameAKey_TheyAgree()
    {
        Dictionary<string, string> keys = StringKeys();
        var compared = 0;

        foreach ((string name, string value) in keys)
        {
            if (!name.StartsWith("PW_KEY_", StringComparison.Ordinal))
                continue;

            string spa = "SPA_KEY_" + name["PW_KEY_".Length..];
            if (!keys.TryGetValue(spa, out string? other))
                continue;

            compared++;
            Assert.AreEqual(value, other, $"{name} and {spa} name different properties");
        }

        Assert.IsTrue(compared > 0, "no overlapping keys were found - the comparison did nothing");
    }
}
