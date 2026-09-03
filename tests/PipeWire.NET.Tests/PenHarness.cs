using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// Long-running adversarial scenarios, meant to be run deliberately against a session that other
/// processes are churning at the same time - not part of an ordinary run.
/// </summary>
/// <remarks>
/// <para>
/// Each scenario runs until its budget expires and reports counters; a hang, an abort, or a
/// read-after-write mismatch is the finding.
/// </para>
/// <para>
/// Carries Integration and RequiresDaemon as well as its own category, which is not redundant:
/// every filter in CI is written as an exclusion of Integration, so a class tagged only PenTest
/// lands in the leg that runs without a daemon and fails on pw_context_connect.
/// </para>
/// <para>
/// Every leg also excludes PenTest by name. These scenarios churn one shared session for seconds at
/// a time - twelve contexts opening at once, metadata written in a tight loop - so anything sharing
/// that session fails on this harness's traffic rather than on anything of its own. Run them
/// deliberately: <c>--filter "TestCategory=PenTest"</c>, with <c>PWNET_PEN_SECONDS</c> to soak.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("PenTest")]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[DoNotParallelize]
[SupportedOSPlatform("linux")]
public sealed class PenHarness
{
    private static readonly object Gate = new();

    /// <summary>Where the counters go, one line per scenario.</summary>
    /// <remarks>
    /// The path is resolved rather than hardcoded so the file lands wherever the host puts temporary
    /// files, and the run is stamped so a reader can tell one append from the last.
    /// </remarks>
    internal static string ReportPath { get; } =
        Path.Combine(Path.GetTempPath(), "pwnet-pen-report.txt");

    /// <summary>
    /// Reports to a file rather than the console: a test host captures stdout and only shows it for
    /// a failing test, and these scenarios report by counter rather than by failing.
    /// </summary>
    private static void Report(string line)
    {
        // UTC. A soak spanning a daylight-saving change otherwise writes an hour that goes
        // backwards, and the file is read by whoever is diagnosing the run rather than by a person
        // in the runner's timezone.
        lock (Gate)
        {
            File.AppendAllText(
                ReportPath,
                $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z pid={Environment.ProcessId} {line}"
                + Environment.NewLine);
        }
    }

    /// <summary>
    /// How long each scenario runs. Short by default so an ordinary suite run still exercises these
    /// paths without paying for a soak; set PWNET_PEN_SECONDS to soak deliberately.
    /// </summary>
    private static int Seconds =>
        int.TryParse(Environment.GetEnvironmentVariable("PWNET_PEN_SECONDS"), out int s) ? s : 5;

    private static CancellationTokenSource Budget()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");

        return new CancellationTokenSource(TimeSpan.FromSeconds(Seconds));
    }

    [TestMethod]
    public async Task Churn()
    {
        using CancellationTokenSource cts = Budget();
        long binds = 0, reads = 0;
        try { (binds, reads) = await ChurnAsync(cts.Token); } catch (OperationCanceledException) { }

        // Asserted here rather than inside the scenario: a budget expiring mid-iteration
        // otherwise skips the report and the check together, and the run passes having
        // tested nothing.
        Assert.IsTrue(binds > 0, "the churn scenario bound nothing; it never reached the graph");
        Assert.IsTrue(reads > 0, "the churn scenario read no parameters from any node");
    }

    [TestMethod]
    public async Task BindAll()
    {
        using CancellationTokenSource cts = Budget();
        long held = 0, ok = 0;
        try { (held, ok) = await BindAllAsync(cts.Token); } catch (OperationCanceledException) { }

        Assert.IsTrue(held > 0, "nothing in the graph could be bound");
        Assert.IsTrue(ok > 0, "every read against every held binding failed");
    }

    [TestMethod]
    public async Task Meta()
    {
        using CancellationTokenSource cts = Budget();
        long writes = 0;
        try { writes = await MetaAsync(cts.Token); } catch (OperationCanceledException) { }

        Assert.IsTrue(writes > 0, "the metadata scenario wrote nothing; it never reached the store");
    }

    [TestMethod]
    public async Task Contexts()
    {
        using CancellationTokenSource cts = Budget();
        long made = 0;
        try { made = await ContextsAsync(cts.Token); } catch (OperationCanceledException) { }

        Assert.IsTrue(made > 0, "no context completed a full open-and-close cycle");
    }

    private static async Task<(long Binds, long Reads)> ChurnAsync(CancellationToken ct)
    {
        await using var ctx = new PipeWireContext("pen-churn");
        await ctx.StartAsync(ct);
        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(ct);

        long reads = 0, errors = 0, binds = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (PipeWireNode node in reg.Current.Nodes)
                {
                    if (ct.IsCancellationRequested) break;
                    PipeWireNodeControl? control = null;
                    try
                    {
                        control = reg.BindNode(node.NodeId);
                        binds++;
                        await control.EnumerateParametersAsync(SpaParamType.Props, ct);
                        reads++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { errors++; if (errors < 6) Report($"PEN churn: {ex.GetType().Name}: {ex.Message}"); }
                    finally { if (control is not null) await control.DisposeAsync(); }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The budget expiring mid-iteration is the normal end of a soak, not a failure.
            // Without this the counters built over the whole run are lost and the caller
            // asserts against zeros.
        }
        finally
        {
            Report($"PEN churn: binds={binds} reads={reads} errors={errors}");
        }
        return (binds, reads);
    }

    private static async Task<(long Held, long Reads)> BindAllAsync(CancellationToken ct)
    {
        await using var ctx = new PipeWireContext("pen-bindall");
        await ctx.StartAsync(ct);
        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(ct);

        var held = new List<PipeWireNodeControl>();
        foreach (PipeWireNode node in reg.Current.Nodes)
        {
            try { held.Add(reg.BindNode(node.NodeId)); } catch (Exception) { }
        }
        Report($"PEN bindall: holding {held.Count} bindings");

        long ok = 0, gone = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (PipeWireNodeControl c in held)
                {
                    if (ct.IsCancellationRequested) break;
                    try { await c.GetVolumeAsync(ct); ok++; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { gone++; }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Budget expiry is the normal end; keep the counters for the caller.
        }
        finally
        {
            foreach (PipeWireNodeControl c in held) await c.DisposeAsync();
            Report($"PEN bindall: reads={ok} failed={gone}");
        }
        return (held.Count, ok);
    }

    // Hammer the metadata store from managed code while pw-metadata hammers it from outside.
    private static async Task<long> MetaAsync(CancellationToken ct)
    {
        await using var ctx = new PipeWireContext("pen-meta");
        await ctx.StartAsync(ct);
        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(ct);

        PipeWireMetadataStore? store = reg.BindMetadataStore("default");
        if (store is null) { Report("PEN meta: no default store"); return 0; }

        await using (store)
        {
            await store.ReadyAsync(ct);
            long writes = 0, events = 0;
            store.EntryChanged += (_, _) => Interlocked.Increment(ref events);

            string key = $"pen.meta.{Environment.ProcessId}";
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await store.SetAsync(key, $"v{writes}", cancellationToken: ct);
                        if (store.Get(key) != $"v{writes}")
                            Report($"PEN meta: READ-AFTER-WRITE MISMATCH at {writes}");
                        writes++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Report($"PEN meta: {ex.GetType().Name}"); break; }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Budget expiry is the normal end; keep the count for the caller.
            }
            finally
            {
                try { await store.SetAsync(key, null, cancellationToken: CancellationToken.None); } catch { }
                Report($"PEN meta: writes={writes} events={Interlocked.Read(ref events)}");
            }
            return writes;
        }
    }

    // Many contexts opening and closing at once, to exhaust descriptors and races in startup.
    private static async Task<long> ContextsAsync(CancellationToken ct)
    {
        long made = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Task[] wave =
                [
                    .. Enumerable.Range(0, 12).Select(i => Task.Run(async () =>
                    {
                        var c = new PipeWireContext($"pen-ctx-{i}");
                        try
                        {
                            await c.StartAsync(ct);
                            var r = new PipeWireRegistry(c);
                            await r.WaitForInitialEnumerationAsync(ct);
                            await r.DisposeAsync();
                            // Only a full cycle counts. Incrementing in a finally would count
                            // attempts that never reached the graph as successes.
                            Interlocked.Increment(ref made);
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { Report($"PEN ctx: {ex.GetType().Name}: {ex.Message}"); }
                        finally { await c.DisposeAsync(); }
                    }, ct)),
                ];
                try { await Task.WhenAll(wave); } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            Report($"PEN contexts: opened/closed {Interlocked.Read(ref made)}");
        }
        return Interlocked.Read(ref made);
    }
}
