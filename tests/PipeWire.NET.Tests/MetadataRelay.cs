using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// Waits for a metadata write to travel from one client to another, and says which hop failed when
/// it does not arrive.
/// </summary>
/// <remarks>
/// <para>
/// The default store is served by the session manager, so a write goes
/// writer to daemon to session manager to daemon to reader. Three of those four hops are somebody
/// else's process, and a bare timeout on the last one says nothing about which of them stalled.
/// </para>
/// <para>
/// The distinction that matters to this library is whether the value reached the reader's store at
/// all. If it did and no event was raised, the event path is the defect and the test fails. If it
/// did not, the session manager never relayed it, and no assertion about this library can be drawn
/// from that.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal static class MetadataRelay
{
    /// <summary>How long to give the session manager before giving up on a relay.</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Waits for <paramref name="key"/> to reach <paramref name="reader"/> through its
    /// <see cref="PipeWireMetadataStore.EntryChanged"/> event.
    /// </summary>
    /// <param name="reader">The store expected to learn about the write.</param>
    /// <param name="key">The key to watch for.</param>
    /// <param name="write">Issues the write, once the listener is attached.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <returns>The value the event carried.</returns>
    internal static async Task<string?> AwaitRelayAsync(
        PipeWireMetadataStore reader,
        string key,
        Func<Task> write,
        CancellationToken cancellationToken)
    {
        var arrived = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnChanged(PipeWireMetadataStore _, PipeWireMetadataEntry entry)
        {
            if (entry.Key == key) arrived.TrySetResult(entry.Value);
        }

        reader.EntryChanged += OnChanged;
        try
        {
            await write().ConfigureAwait(false);

            try
            {
                return await arrived.Task.WaitAsync(Budget, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                string? inStore = reader.Get(key);
                if (inStore is not null)
                {
                    Assert.Fail(
                        $"the reader's store holds '{key}' = '{inStore}' but never raised the change, "
                        + "so the event path dropped it");
                }

                Assert.Inconclusive(
                    $"the session manager did not relay '{key}' to a second client within {Budget}. "
                    + "The value never reached the reader's store either, so nothing about this "
                    + "library can be concluded from it.");
                throw;   // unreachable; Inconclusive throws
            }
        }
        finally
        {
            reader.EntryChanged -= OnChanged;
        }
    }
}
