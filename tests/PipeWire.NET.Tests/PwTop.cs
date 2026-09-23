using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PipeWire.NET.Tests;

/// <summary>
/// Reads <c>pw-top</c>, which is the graph's own account of how well it ran.
/// </summary>
/// <remarks>
/// <para>
/// The tests here can tell whether frames arrived and whether their timestamps line up. They cannot
/// tell what it cost: a graph that is missing its deadline drops or repeats cycles, and the frames
/// that do arrive still look perfectly ordered. That is the difference between "the streams are in
/// sync" and "the streams are in sync and the graph was not starving to keep them there".
/// </para>
/// <para>
/// <c>pw-top -b</c> prints one batch per interval, a header row then one row per node:
/// </para>
/// <code>
/// S   ID  QUANT   RATE    WAIT    BUSY   W/Q   B/Q  ERR FORMAT           NAME
/// R   62   1024  48000  12.3us  40.1us  0.02  0.07    0                  my-node
/// </code>
/// <para>
/// <c>ERR</c> is the node's cumulative xrun count. It is the column worth asserting on.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal static class PwTop
{
    /// <summary>One node's row in a <c>pw-top</c> batch.</summary>
    internal sealed record Row(uint Id, string Name, int Quantum, int Rate, long Errors);

    private static string? Resolve()
    {
        if (Environment.GetEnvironmentVariable("PWNET_TEST_PW_TOP") is { Length: > 0 } overridden)
            return File.Exists(overridden) ? overridden : null;

        foreach (
            string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries
            )
        )
        {
            string candidate = Path.Combine(dir, "pw-top");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static readonly string? Tool = Resolve();

    /// <summary>True when <c>pw-top</c> is installed.</summary>
    public static bool IsAvailable { get; } = OperatingSystem.IsLinux() && Tool is not null;

    /// <summary>Skips the calling test when <c>pw-top</c> is not installed.</summary>
    public static void Require()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
        if (!IsAvailable)
            Assert.Inconclusive("pw-top not present.");
    }

    /// <summary>
    /// Takes <paramref name="batches"/> readings and returns the last complete one.
    /// </summary>
    /// <remarks>
    /// The last, not the first: the opening batch is a snapshot taken before any cycle has been
    /// measured, so its timing columns read as <c>---</c> and its counters are whatever the node
    /// carried when pw-top attached.
    /// </remarks>
    public static async Task<IReadOnlyList<Row>> ReadAsync(int batches, CancellationToken ct)
    {
        Require();

        var psi = new ProcessStartInfo(Tool!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-b");
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add(Math.Max(2, batches).ToString(CultureInfo.InvariantCulture));

        using Process proc =
            Process.Start(psi) ?? throw new InvalidOperationException("could not start pw-top.");

        string stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        var current = new List<Row>();
        var last = new List<Row>();

        foreach (string raw in stdout.Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0)
                continue;

            // A header row starts a new batch. Recognised by its QUANT column only: its leading "S"
            // is also the state letter of every suspended node's row, and on a desktop session full
            // of idle devices that split each batch and dropped the rows before every one of them.
            if (line.Contains("QUANT", StringComparison.Ordinal))
            {
                if (current.Count > 0)
                {
                    last = current;
                    current = [];
                }
                continue;
            }

            if (Parse(line) is { } row)
                current.Add(row);
        }

        return current.Count > 0 ? current : last;
    }

    private static Row? Parse(string line)
    {
        // S   ID  QUANT   RATE    WAIT    BUSY   W/Q   B/Q  ERR FORMAT  NAME
        // 0   1   2       3       4       5      6     7    8   9...    rest
        string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length < 9)
            return null;
        if (!uint.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint id))
            return null;

        _ = int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int quantum);
        _ = int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rate);

        if (
            !long.TryParse(
                f[8],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long errors
            )
        )
            return null;

        // The name is the last field; FORMAT may be blank, so counting from the end is safer than
        // counting from the start.
        string name = f[^1];

        return new Row(id, name, quantum, rate, errors);
    }

    /// <summary>The xrun count for a node, or null when pw-top did not report that node.</summary>
    public static async Task<long?> ErrorsForAsync(uint nodeId, CancellationToken ct)
    {
        IReadOnlyList<Row> rows = await ReadAsync(3, ct);
        return rows.FirstOrDefault(r => r.Id == nodeId)?.Errors;
    }
}
