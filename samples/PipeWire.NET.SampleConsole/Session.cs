using System.Runtime.Versioning;
using PipeWire.NET.Graph;
using PipeWire.NET;

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

    public static async Task<Session> ConnectAsync(string name, CancellationToken cancellationToken)
    {
        var context = new PipeWireContext(name);
        try
        {
            await context.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await context.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        Console.WriteLine("  connected to daemon.");

        var registry = new PipeWireRegistry(context);
        try
        {
            // A bounded wait, not the bare call: on an empty graph (headless CI) enumeration
            // still completes, but a missing daemon must fail fast instead of hanging the sample.
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, bound.Token);
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
