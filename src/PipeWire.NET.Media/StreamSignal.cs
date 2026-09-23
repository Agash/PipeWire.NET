using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Media;

/// <summary>
/// Awaits a one-shot signal from the loop thread. Separate from the stream because that class is
/// unsafe, and an await cannot appear in an unsafe context.
/// </summary>
internal static class StreamSignal
{
    internal static async Task AwaitAsync(
        TaskCompletionSource done,
        CancellationToken cancellationToken
    )
    {
        using CancellationTokenRegistration reg = cancellationToken.Register(
            static s => ((TaskCompletionSource)s!).TrySetCanceled(),
            done
        );
        await done.Task.ConfigureAwait(false);
    }
}
