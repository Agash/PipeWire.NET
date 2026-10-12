using System.Runtime.Versioning;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// WirePlumber's runtime settings (the <c>sm-settings</c> metadata), changed for one test and put back
/// as they were afterwards.
/// </summary>
/// <remarks>
/// <para>
/// WirePlumber watches this store and reacts to a change as it reads it: <c>device.restore-routes</c>,
/// for one, unregisters the hooks that store and restore a card's route volumes. Writing
/// <c>sm-settings</c> rather than <c>persistent-sm-settings</c> lasts for the running session only.
/// </para>
/// <para>
/// The change is in effect for anything this test does after the override returns: the write has been
/// processed by the daemon by then, and the daemon hands WirePlumber the change on its one connection
/// ahead of the events the test's later requests cause, which WirePlumber handles in order.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal static class SessionSettings
{
    /// <summary>Sets a setting until the returned handle is disposed, then restores what it was.</summary>
    /// <param name="registry">A registry on a session with WirePlumber.</param>
    /// <param name="key">The setting, such as <c>device.restore-routes</c>.</param>
    /// <param name="json">Its value for the test, as JSON.</param>
    /// <param name="cancellationToken">Abandons the write.</param>
    /// <returns>The override; disposing it restores the setting, or removes it when there was none.</returns>
    public static async Task<IAsyncDisposable> OverrideAsync(
        PipeWireRegistry registry,
        string key,
        string json,
        CancellationToken cancellationToken
    )
    {
        PipeWireMetadataProxy? settings = await registry.BindMetadataWhenPresentAsync(
            "sm-settings",
            TimeSpan.FromSeconds(5),
            cancellationToken
        );
        if (settings is null)
            Assert.Inconclusive("no session manager settings store on this session.");

        await settings.ReadyAsync(cancellationToken);
        PipeWireMetadataEntry? original = settings.Entries.FirstOrDefault(e =>
            e.Subject == PipeWireMetadataProxy.SubjectCore && e.Key == key
        );
        await settings.SetAsync(key, json, "Spa:String:JSON", cancellationToken: cancellationToken);
        return new Override(settings, key, original);
    }

    private sealed class Override(
        PipeWireMetadataProxy settings,
        string key,
        PipeWireMetadataEntry? original
    ) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await using (settings)
            {
                await settings.SetAsync(key, original?.Value, original?.Type);
            }
        }
    }
}
