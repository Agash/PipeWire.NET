using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// Removes the metadata keys the suite wrote, once the whole run is over.
/// </summary>
/// <remarks>
/// The daemon's metadata store outlives the test process, and nothing else ever clears it, so keys
/// left behind accumulate and confuse tests that assert on the contents of the default store.
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public static class SessionHygiene
{
    /// <summary>The pen harness names its keys after itself rather than after the suite.</summary>
    private static readonly string[] TestKeyPrefixes = ["pwnet.", "pen."];

    private static bool IsOurs(string key)
    {
        foreach (string prefix in TestKeyPrefixes)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    [AssemblyCleanup]
    public static async Task RemoveOurMetadataKeysAsync()
    {
        if (!OperatingSystem.IsLinux()) return;

        // Nothing to clean after a run that never had a daemon, and connecting only to find that
        // out costs the whole 30-second budget on a host that has none. The unit leg runs here too.
        if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is not { Length: > 0 } runtimeDir
            || !Directory.EnumerateFiles(runtimeDir, "pipewire-*").Any())
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using var ctx = new PipeWireContext("pwnet-hygiene", ConsoleTestLoggerFactory.Instance);
            await ctx.StartAsync(cts.Token);
            await using var registry = new PipeWireRegistry(ctx);
            await registry.WaitForInitialEnumerationAsync(cts.Token);

            PipeWireMetadataStore? store = registry.BindMetadataStore("default");
            if (store is null) return;

            await using (store)
            {
                await store.ReadyAsync(cts.Token);

                foreach (PipeWireMetadataEntry entry in store.Entries)
                {
                    if (!IsOurs(entry.Key)) continue;

                    // A null value is the removal. One key failing to clear must not stop the rest.
                    try
                    {
                        await store.SetAsync(entry.Key, null, subject: entry.Subject,
                            cancellationToken: cts.Token);
                    }
                    catch (PipeWireException)
                    {
                        // Not ours to remove on this daemon; nothing to do but leave it.
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Best-effort cleanup on the way out; a slow session is not a test failure.
        }
        catch (PipeWireException)
        {
            // No daemon, or no permission to reach the store. Neither is a test failure.
        }
    }
}
