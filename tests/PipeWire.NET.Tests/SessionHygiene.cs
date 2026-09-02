using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// Removes the metadata keys the suite wrote, once the whole run is over.
/// </summary>
/// <remarks>
/// The daemon's metadata store outlives the test process, and nothing else ever clears it: every
/// run used to leave its keys behind, so a machine used for a night of testing accumulated dozens
/// of them. That is worth cleaning up on its own, and it also removes a confusing failure mode -
/// tests that assert on the contents of the default store see the debris from every previous run.
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public static class SessionHygiene
{
    /// <summary>The prefix every key this suite writes begins with.</summary>
    private const string TestKeyPrefix = "pwnet.";

    [AssemblyCleanup]
    public static async Task RemoveOurMetadataKeysAsync()
    {
        if (!OperatingSystem.IsLinux()) return;

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
                    if (!entry.Key.StartsWith(TestKeyPrefix, StringComparison.Ordinal)) continue;

                    // A null value is the removal. One key failing to clear must not stop the rest.
                    try
                    {
                        await store.SetAsync(entry.Key, null, subject: entry.Subject,
                            cancellationToken: cts.Token);
                    }
                    catch (InvalidOperationException)
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
        catch (InvalidOperationException)
        {
            // No daemon, or no permission to reach the store. Neither is a test failure.
        }
    }
}
