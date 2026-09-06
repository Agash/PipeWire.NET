using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// Session gates: version floors and facilities tests may assume.
/// </summary>
/// <remarks>
/// Two different reasons share one file. Racy destroy-during-create traffic aborts PipeWire
/// before 1.6.8 (use-after-free in pw_global_destroy, proven against 1.0.5), and a dead daemon
/// fails every test after it. Separately, a few behaviors changed between releases: 1.0.5 emits
/// the current volume as the first subscribed event where 1.6.8 emits only the change, and it
/// reports a fresh filter as driving where 1.6.8 does not. None of that is this library
/// misbehaving, so on older daemons these tests go Inconclusive rather than red.
/// The version comes from the session (PWNET_DAEMON_VERSION, published by build/session.sh),
/// because the daemon reports it in core info, which never reaches global props. Without it
/// (local runs) the tests execute.
///
/// Third-party loopback streams only get ports once the session manager links them, which needs
/// an audio sink and an audio source to route to. Headless sessions have neither, so tests that
/// wait on loopback ports go Inconclusive there rather than timing out.
/// </remarks>
[SupportedOSPlatform("linux")]
internal static class SessionGates
{
    internal static void RequireDaemonAtLeast(int major, int minor, int patch)
    {
        string? reported = Environment.GetEnvironmentVariable("PWNET_DAEMON_VERSION");
        if (reported is null) return;
        if (CompareVersions(reported, major, minor, patch) < 0)
            Assert.Inconclusive(
                $"needs a PipeWire daemon at least {major}.{minor}.{patch}, " +
                $"this session reports {reported}.");
    }

    /// <summary>
    /// Loopback streams only get ports once the session manager links them somewhere, which
    /// needs an audio sink and an audio source to route to. Headless sessions have neither.
    /// Waits a little first: hardware enumeration lags session startup, and a slow card is not
    /// the same as no card.
    /// </summary>
    internal static async Task RequireAudioRouteAsync(
        PipeWireRegistry registry, CancellationToken cancellationToken)
    {
        if (HasAudioRoute(registry.Current)) return;

        // Bounded by its own clock, not the caller's: eating the whole test budget here would
        // leave nothing for the waits the test itself still needs afterwards.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);
        try
        {
            while (!HasAudioRoute(registry.Current))
                await Task.Delay(250, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested
                                                 && !cancellationToken.IsCancellationRequested)
        {
            // Our own timeout and nobody else's: fall through to Inconclusive below.
            // Anything else (notably the caller's own cancellation) keeps propagating.
        }

        if (!HasAudioRoute(registry.Current))
            Assert.Inconclusive(
                "this session offers no audio route for loopback streams (headless, no devices).");
    }

    /// <summary>Whether the session has an audio sink and an audio source to route through.</summary>
    internal static bool HasAudioRoute(PipeWireGraphSnapshot graph)
    {
        bool sink = false;
        bool source = false;
        foreach (PipeWireNode node in graph.Nodes)
        {
            if (node.Media != PipeWireMediaKind.Audio) continue;
            if (node.Flow == PipeWireMediaFlow.Sink) sink = true;
            if (node.Flow == PipeWireMediaFlow.Source) source = true;
        }

        return sink && source;
    }

    private static int CompareVersions(string version, int major, int minor, int patch)
    {
        int[] want = [major, minor, patch];
        int[] got = [0, 0, 0];
        int part = 0;
        int value = 0;
        bool digits = false;
        foreach (char c in version)
        {
            if (c is >= '0' and <= '9')
            {
                value = (value * 10) + (c - '0');
                digits = true;
            }
            else if (c == '.' && digits)
            {
                if (part < 3) got[part] = value;
                part++;
                value = 0;
                digits = false;
            }
            else
            {
                break;
            }
        }

        if (digits && part < 3) got[part] = value;

        for (int i = 0; i < 3; i++)
        {
            if (got[i] != want[i]) return got[i].CompareTo(want[i]);
        }

        return 0;
    }
}
