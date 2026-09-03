using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PipeWire.NET.Tests;

/// <summary>
/// The shipped public surface, and the documentation that claims to describe it.
/// </summary>
/// <remarks>
/// Two things rot silently. A rename lands everywhere the compiler can see and nowhere it cannot,
/// so README samples keep naming members that no longer exist. And a 0.x package has no compiler
/// check on its own surface at all, so a type going public by accident ships as a promise. Both are
/// cheap to catch here and expensive to catch after a release.
/// </remarks>
[TestClass]
public sealed class PublicSurfaceTests
{
    private static readonly Assembly[] Shipped =
    [
        typeof(Graph.PipeWireRegistry).Assembly,
        typeof(Media.Streams.PipeWireAudioCapture).Assembly,
    ];

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PipeWire.NET.slnx")))
            dir = dir.Parent;

        Assert.IsNotNull(dir, "could not find the repository root from the test output directory");
        return dir!.FullName;
    }

    /// <summary>Every public type and member, one per line, ordered so the diff is readable.</summary>
    private static string RenderSurface()
    {
        var lines = new List<string>();

        foreach (Assembly assembly in Shipped)
        {
            foreach (Type type in assembly.GetExportedTypes())
            {
                if (IsCompilerGenerated(type)) continue;

                lines.Add(type.FullName!);

                foreach (MemberInfo member in type.GetMembers(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    // Property and event accessors are already implied by the property or event.
                    if (member is MethodInfo { IsSpecialName: true }) continue;
                    if (IsCompilerGenerated(member)) continue;

                    lines.Add($"{type.FullName}.{Signature(member)}");
                }
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines) + '\n';
    }

    private static string Signature(MemberInfo member) => member switch
    {
        MethodBase method =>
            $"{method.Name}({string.Join(", ", method.GetParameters().Select(p => Render(p.ParameterType)))})",
        PropertyInfo property => $"{property.Name} : {Render(property.PropertyType)}",
        FieldInfo field => $"{field.Name} = {Render(field.FieldType)}",
        EventInfo evt => $"{evt.Name} event",
        _ => member.Name,
    };

    /// <summary>A type name that tells two generic instantiations apart.</summary>
    /// <remarks>
    /// Type.Name renders both ReadOnlySpan&lt;byte&gt; and ReadOnlySpan&lt;VideoPlane&gt; as
    /// ReadOnlySpan`1, so two overloads differing only in their element type collapse to one line
    /// and the baseline cannot see one of them being removed.
    /// </remarks>
    private static string Render(Type type)
    {
        if (!type.IsGenericType) return type.Name;

        string name = type.Name;
        int tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0) name = name[..tick];

        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(Render))}>";
    }

    [TestMethod]
    public void ThePublicSurface_MatchesTheCheckedInBaseline()
    {
        string baselinePath = Path.Combine(RepoRoot(), "PublicAPI.txt");
        string actual = RenderSurface();

        if (!File.Exists(baselinePath))
        {
            // Locally, writing it is the convenience that makes the baseline maintainable: delete
            // the file, run, review the diff, commit. On a build server it is the opposite - a
            // missing baseline there means it was never committed, and quietly generating one
            // turns the whole check into a skip that reads as a pass.
            if (Environment.GetEnvironmentVariable("CI") is { Length: > 0 })
                Assert.Fail($"{baselinePath} is missing. It is a committed file, not a generated one.");

            File.WriteAllText(baselinePath, actual, new UTF8Encoding(false));
            Assert.Inconclusive($"wrote a first baseline to {baselinePath}; review and commit it.");
        }

        string expected = File.ReadAllText(baselinePath).ReplaceLineEndings("\n");

        if (expected == actual) return;

        // Show what moved rather than two thousand lines of file.
        var before = new HashSet<string>(expected.Split('\n'), StringComparer.Ordinal);
        var after = new HashSet<string>(actual.Split('\n'), StringComparer.Ordinal);

        IEnumerable<string> removed = before.Except(after).Order(StringComparer.Ordinal).Select(l => $"  - {l}");
        IEnumerable<string> added = after.Except(before).Order(StringComparer.Ordinal).Select(l => $"  + {l}");

        Assert.Fail(
            "the public surface changed. Intentional? Regenerate PublicAPI.txt by deleting it and "
            + $"re-running this test.\n{string.Join('\n', removed.Concat(added))}");
    }

    [TestMethod]
    public void EveryMemberTheReadmeNames_ExistsInTheShippedSurface()
    {
        string readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));

        var known = new HashSet<string>(StringComparer.Ordinal);
        var membersByType = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (Assembly assembly in Shipped)
        {
            foreach (Type type in assembly.GetExportedTypes())
            {
                known.Add(type.Name);

                HashSet<string> members = membersByType.TryGetValue(type.Name, out HashSet<string>? existing)
                    ? existing
                    : membersByType[type.Name] = new HashSet<string>(StringComparer.Ordinal);

                foreach (MemberInfo member in type.GetMembers())
                {
                    known.Add(member.Name);
                    members.Add(member.Name);
                }
            }
        }

        var missing = new List<string>();

        foreach (string block in CodeBlocks(readme))
        {
            // A member access on a lowercase receiver: `frame.Width`, `source.MediaClass`. Anything
            // starting uppercase is a type or namespace and is checked by the compiler in samples,
            // or is prose like a D-Bus interface name.
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(block, @"\b[a-z][A-Za-z0-9_]*\.([A-Z][A-Za-z0-9_]*)"))
            {
                string name = m.Groups[1].Value;
                if (!known.Contains(name) && !missing.Contains(name)) missing.Add(name);
            }

            // Static access on one of our own types: PipeWireMediaFlow.Source. The pattern above
            // needs a lowercase receiver, so it skipped these entirely, and a README naming an enum
            // member that does not exist shipped that way. Only receivers that are themselves
            // shipped types are checked, which leaves System.Threading and the like alone.
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(block, @"\b([A-Z][A-Za-z0-9_]*)\.([A-Z][A-Za-z0-9_]*)"))
            {
                if (!membersByType.TryGetValue(m.Groups[1].Value, out HashSet<string>? members)) continue;

                string member = m.Groups[2].Value;
                string qualified = $"{m.Groups[1].Value}.{member}";
                if (!members.Contains(member) && !missing.Contains(qualified)) missing.Add(qualified);
            }
        }

        Assert.IsTrue(missing.Count == 0,
            $"the README names members that do not exist: {string.Join(", ", missing)}");
    }

    [TestMethod]
    public void EveryPublicControl_CanBeObtainedFromSomewhereInThePublicSurface()
    {
        // A public type with no factory reachable from the surface cannot be obtained by any
        // caller, and neither the compiler nor the package baseline has an opinion about that.
        var factories = new HashSet<string>(StringComparer.Ordinal);

        foreach (Assembly assembly in Shipped)
        {
            foreach (Type type in assembly.GetExportedTypes())
            {
                foreach (MethodInfo method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    factories.Add(Unwrap(method.ReturnType).Name);
                }

                foreach (PropertyInfo property in type.GetProperties(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    factories.Add(Unwrap(property.PropertyType).Name);
                }
            }
        }

        var unreachable = new List<string>();

        foreach (Assembly assembly in Shipped)
        {
            foreach (Type type in assembly.GetExportedTypes())
            {
                // Every bound-object type, identified by what makes it one: an internal static
                // Bind and no public constructor. Naming them by suffix missed the ones that are
                // not called Control, and an unreachable type is exactly the kind that gets missed.
                if (type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length > 0) continue;
                if (type.GetMethod("Bind", BindingFlags.Static | BindingFlags.NonPublic) is null) continue;

                if (!factories.Contains(type.Name)) unreachable.Add(type.Name);
            }
        }

        Assert.IsTrue(unreachable.Count == 0,
            "these public types cannot be obtained from anywhere in the public surface: "
            + string.Join(", ", unreachable));
    }

    /// <summary>Whether a type is the compiler's, not the library's.</summary>
    /// <remarks>
    /// The C# extension-block syntax emits holder types whose names cannot be written in C#, so
    /// reflection reports them as public while nothing can name them. Recording those in the
    /// baseline means every recompile of an extension block churns it for no change anyone can see.
    /// </remarks>
    private static bool IsCompilerGenerated(Type type) =>
        type.FullName is null
        || type.FullName.Contains('<', StringComparison.Ordinal)
        || type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false);

    /// <summary>Whether a member is the compiler's, not the library's.</summary>
    /// <remarks>
    /// A record's clone method and the extension holders are both spelled with angle brackets, and
    /// the holder names carry a content hash that changes whenever the block is recompiled. Nothing
    /// can call either, so recording them makes the baseline churn on edits that change no surface.
    /// </remarks>
    private static bool IsCompilerGenerated(MemberInfo member) =>
        member.Name.Contains('<', StringComparison.Ordinal);

    /// <summary>Looks through Task, ValueTask and Nullable to the type actually handed back.</summary>
    private static Type Unwrap(Type type)
    {
        while (type.IsGenericType)
        {
            Type definition = type.GetGenericTypeDefinition();
            if (definition != typeof(Task<>) && definition != typeof(ValueTask<>) && definition != typeof(Nullable<>))
                break;

            type = type.GetGenericArguments()[0];
        }

        return type;
    }

    private static IEnumerable<string> CodeBlocks(string markdown)
    {
        string[] lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var current = new StringBuilder();
        bool inCSharp = false;

        foreach (string line in lines)
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (inCSharp)
                {
                    yield return current.ToString();
                    current.Clear();
                    inCSharp = false;
                }
                else
                {
                    inCSharp = line.Contains("csharp", StringComparison.Ordinal)
                            || line.Contains("cs", StringComparison.Ordinal);
                }

                continue;
            }

            if (inCSharp) current.AppendLine(line);
        }
    }
}
