using System.Runtime.Versioning;
using PipeWire.NET;
using PipeWire.NET.Graph;

namespace PipeWire.NET.SampleConsole;

// One shared setup: a started context plus an enumerated registry, torn down in the order the
// library expects (registry first, context last). Every command prints "connected to daemon"
// once the context is up, which is the line CI greps for on the headless run.
[SupportedOSPlatform("linux")]
internal sealed class Session : IAsyncDisposable
{
    public PipeWireContext Context { get; }

    public PipeWireRegistry Registry { get; }

    private Session(PipeWireContext context, PipeWireRegistry registry)
    {
        Context = context;
        Registry = registry;
    }

    public static Task<Session> ConnectAsync(string name, CancellationToken cancellationToken) =>
        ConnectAsync(name, null, cancellationToken);

    /// <param name="name">The client name the daemon sees.</param>
    /// <param name="onConnectionLost">
    /// Cancelled when the daemon goes away, for the commands that run until Ctrl+C. Without it
    /// `monitor` and `serve` sit waiting on a connection that is never coming back.
    /// </param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    public static async Task<Session> ConnectAsync(
        string name,
        CancellationTokenSource? onConnectionLost,
        CancellationToken cancellationToken
    )
    {
        var context = new PipeWireContext(name);
        try
        {
            // Bounded, like the enumeration below. The only token here is the one Ctrl+C cancels,
            // so a daemon that accepts the socket and never finishes the handshake would otherwise
            // leave the command waiting for ever with nothing on screen to say why.
            using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                bounded.Token
            );

            try
            {
                await context.StartAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "the daemon did not complete the connection within 15s."
                );
            }
        }
        catch
        {
            await context.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        Console.WriteLine("  connected to daemon.");

        if (onConnectionLost is not null)
        {
            context.ConnectionLost += fault =>
            {
                Console.Error.WriteLine($"  the daemon connection was lost: {fault.Message}");
                try
                {
                    onConnectionLost.Cancel();
                }
                catch (ObjectDisposedException)
                { /* the command already finished */
                }
            };
        }

        var registry = new PipeWireRegistry(context);
        try
        {
            // A bounded wait, not the bare call: on an empty graph (headless CI) enumeration
            // still completes, but a missing daemon must fail fast instead of hanging the sample.
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                bound.Token
            );
            try
            {
                await registry.WaitForInitialEnumerationAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine("  (no globals reported in 10s - graph may be empty)");
            }
        }
        catch
        {
            await registry.DisposeAsync().ConfigureAwait(false);
            await context.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new Session(context, registry);
    }

    public async ValueTask DisposeAsync()
    {
        await Registry.DisposeAsync().ConfigureAwait(false);
        await Context.DisposeAsync().ConfigureAwait(false);
    }
}
