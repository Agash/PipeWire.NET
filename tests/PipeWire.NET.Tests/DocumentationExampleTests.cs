using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PipeWire.NET.Tests;

/// <summary>
/// Every C# example in the README and the guides, put through a compiler.
/// </summary>
/// <remarks>
/// <para>
/// Documentation examples are the one piece of code in the repository that nothing compiles, so a
/// rename lands everywhere the compiler can see and leaves the examples naming members that no
/// longer exist. Checking the names by pattern was tried and thrown away: it either hand-maintains
/// a list of what to look for, or it guesses, and a guess that is wrong in the safe direction
/// passes a broken example.
/// </para>
/// <para>
/// So the examples are compiled. Each block becomes the body of one method against the shipped
/// assemblies, which catches a renamed member, a changed signature, a wrong argument type and a
/// moved namespace alike. Nothing is executed - these examples talk to a daemon.
/// </para>
/// <para>
/// Names an example uses without declaring (a <c>cancellationToken</c>, a <c>nodeId</c>, the
/// application's own <c>Render</c>) are declared once in <see cref="Harness"/>, as fields so that
/// an example redeclaring one as a local is legal rather than CS0136. An example that needs
/// something else has to say so there, which is the intended friction: an example resting on
/// context the reader does not have is a bad example.
/// </para>
/// <para>
/// To exclude a block - one that is deliberately incomplete, or pseudocode - put
/// <c>&lt;!-- no-compile: why --&gt;</c> on the line before its fence.
/// </para>
/// </remarks>
[TestClass]
public sealed class DocumentationExampleTests : PipeWireTestBase
{
    /// <summary>Declarations the examples use without introducing them.</summary>
    private const string Harness = """
        #pragma warning disable CS0649, CS8618, IDE0059, CS0219
        using System;
        using System.Runtime.InteropServices;
        using System.Threading;
        using System.Threading.Tasks;
        using PipeWire.NET;
        using PipeWire.NET.Graph;
        using PipeWire.NET.Media;
        using PipeWire.NET.Spa;

        internal static class DocExamples
        {
            // An example reads the surrounding application; these stand in for it.
            private static PipeWireContext ctx;
            private static PipeWireRegistry registry;
            private static CancellationToken cancellationToken;
            private static SafeHandle fd;
            private static uint nodeId;
            private static uint deviceId;
            private static uint outputPortId;
            private static uint inputPortId;

            // The application's own code, which an example calls but does not show.
            private static int WriteTone(Span<byte> samples, int sampleRate, int channels) => 0;
            private static bool Render(Span<byte> pixels, int stride, int width, int height) => true;
        """;

    private sealed record Example(string File, int Line, string Code);

    private static List<Example> Collect()
    {
        string root = PublicSurfaceTests.RepoRoot();
        var found = new List<Example>();

        var files = new List<string> { Path.Combine(root, "README.md") };
        files.AddRange(Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md").Order(StringComparer.Ordinal));

        foreach (string path in files)
        {
            string text = File.ReadAllText(path).ReplaceLineEndings("\n");

            foreach (Match m in Regex.Matches(text, @"(?<opt><!--\s*no-compile:[^>]*-->\s*\n)?```csharp\n(?<code>.*?)```", RegexOptions.Singleline))
            {
                if (m.Groups["opt"].Success) continue;

                int line = text[..m.Index].Count(c => c == '\n') + 1;
                found.Add(new Example(Path.GetFileName(path), line, m.Groups["code"].Value));
            }
        }

        return found;
    }

    [TestMethod]
    public void EveryExampleInTheDocumentation_Compiles()
    {
        List<Example> examples = Collect();

        Assert.IsTrue(
            examples.Count > 5,
            $"only {examples.Count} examples were found, so the fences or the paths moved and this "
            + "test is checking nothing");

        string root = PublicSurfaceTests.RepoRoot();

        // Inside the repository, so the generated project resolves the same SDK through global.json
        // as everything else; obj/ is ignored.
        string dir = Path.Combine(root, "obj", "doc-examples");
        Directory.CreateDirectory(dir);

        var code = new StringBuilder(Harness);
        for (var i = 0; i < examples.Count; i++)
        {
            Example example = examples[i];
            code.AppendLine();
            code.AppendLine($"    // {example.File}:{example.Line}");
            code.AppendLine($"    private static async Task Example{i}()");
            code.AppendLine("    {");
            foreach (string line in example.Code.TrimEnd().Split('\n'))
                code.AppendLine(line.Length == 0 ? string.Empty : "        " + line);

            // Every example is awaited into, and some have nothing to await; saying so here keeps
            // the examples themselves free of scaffolding.
            code.AppendLine("        await Task.CompletedTask;");
            code.AppendLine("    }");
        }

        code.AppendLine("}");

        File.WriteAllText(Path.Combine(dir, "Examples.cs"), code.ToString(), new UTF8Encoding(false));

        string tfm = new DirectoryInfo(AppContext.BaseDirectory).Name;

        var paths = new List<string>();
        foreach (string dll in new[] { "PipeWire.NET.dll", "PipeWire.NET.Media.dll" })
        {
            string path = Path.Combine(AppContext.BaseDirectory, dll);
            Assert.IsTrue(File.Exists(path), $"{dll} is not beside the tests, so nothing can be compiled against it");
            paths.Add(path);
        }

        // A context takes an ILoggerFactory, so an example that builds one is CS0012 without this.
        // Only on the frameworks that carry it locally: net11 resolves it from the shared framework
        // and has no copy beside the tests.
        string logging = Path.Combine(AppContext.BaseDirectory, "Microsoft.Extensions.Logging.Abstractions.dll");
        if (File.Exists(logging)) paths.Add(logging);

        string references = string.Join('\n', paths.Select(
            p => $"""    <Reference Include="{Path.GetFileNameWithoutExtension(p)}"><HintPath>{p}</HintPath></Reference>"""));

        // Analyzers and warnings-as-errors are the repository's rules for its own code. An example
        // is written to be read, so it is held to compiling, not to CA1303.
        File.WriteAllText(Path.Combine(dir, "doc-examples.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{tfm}</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>disable</ImplicitUsings>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <EnableNETAnalyzers>false</EnableNETAnalyzers>
                <EnforceCodeStyleInBuild>false</EnforceCodeStyleInBuild>
                <RunAnalyzers>false</RunAnalyzers>
                <GenerateDocumentationFile>false</GenerateDocumentationFile>
                <NoWarn>CS1701;CS1702</NoWarn>
              </PropertyGroup>
              <ItemGroup>
            {references}
              </ItemGroup>
            </Project>
            """, new UTF8Encoding(false));

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in new[] { "build", "doc-examples.csproj", "--nologo", "-v", "q" })
            psi.ArgumentList.Add(arg);

        using Process? build = Process.Start(psi);
        Assert.IsNotNull(build, "could not start dotnet to compile the examples");

        string output = build!.StandardOutput.ReadToEnd() + build.StandardError.ReadToEnd();
        Assert.IsTrue(build.WaitForExit(milliseconds: 300_000), "compiling the examples did not finish");

        if (build.ExitCode == 0) return;

        // Point at the markdown, not at the generated file nobody will open: each example carries a
        // comment naming its file and line, and the compiler's line numbers are into the generated
        // file, so the mapping is done here.
        string[] generated = File.ReadAllLines(Path.Combine(dir, "Examples.cs"));
        var reported = new List<string>();

        foreach (Match m in Regex.Matches(output, @"Examples\.cs\((?<line>\d+),\d+\): (?<rest>error .*)"))
        {
            var line = int.Parse(m.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
            string origin = "unknown example";
            for (int i = Math.Min(line, generated.Length) - 1; i >= 0; i--)
            {
                if (!generated[i].TrimStart().StartsWith("// ", StringComparison.Ordinal)) continue;
                origin = generated[i].Trim(' ', '/');
                break;
            }

            reported.Add($"  {origin}: {m.Groups["rest"].Value.Trim()}");
        }

        Assert.Fail(
            "documentation examples do not compile against the shipped surface:\n"
            + (reported.Count > 0 ? string.Join('\n', reported.Distinct()) : output));
    }
}
