using System.Collections.Immutable;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// Waits that end on the event they wait for. Each test bounds them with its budget token, and a wait
/// that runs out of budget fails with what it was waiting for.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal static class Waits
{
    /// <summary>Fails the test with <paramref name="because"/> when the wait is cancelled or times out.</summary>
    public static async Task<T> OrFail<T>(this Task<T> wait, string because)
    {
        try
        {
            return await wait.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            throw new AssertFailedException(because);
        }
    }

    /// <inheritdoc cref="OrFail{T}(Task{T}, string)"/>
    public static async Task OrFail(this Task wait, string because)
    {
        try
        {
            await wait.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            throw new AssertFailedException(because);
        }
    }

    /// <summary>
    /// Completes on the capture's next frame. A frame the capture retains is stored before
    /// <c>FrameReady</c> is raised, so it can be taken once this completes.
    /// </summary>
    public static Task NextFrame(this PipeWireVideoCapture capture)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(PipeWireVideoCapture sender, VideoFrame frame)
        {
            sender.FrameReady -= OnFrame;
            arrived.TrySetResult();
        }

        capture.FrameReady += OnFrame;
        return arrived.Task;
    }

    /// <summary>
    /// Completes once an entry of a bound store satisfies <paramref name="holds"/>: checked now and
    /// on every change the store reports, removals included.
    /// </summary>
    public static Task UntilEntry(
        this PipeWireMetadataProxy store,
        string key,
        Func<string?, bool> holds
    )
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChange(PipeWireMetadataProxy sender, PipeWireMetadataEntry entry)
        {
            if (holds(sender.Get(key)))
            {
                sender.EntryChanged -= OnChange;
                reached.TrySetResult();
            }
        }

        store.EntryChanged += OnChange;
        OnChange(store, new PipeWireMetadataEntry(0, key, null, null));
        return reached.Task;
    }

    /// <summary>
    /// Completes once the serving side of a store holds a value for a key that satisfies
    /// <paramref name="holds"/>: checked now and on every change it is told of.
    /// </summary>
    public static Task UntilEntry(
        this PipeWireMetadataProvider provider,
        string key,
        Func<string?, bool> holds
    )
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChange(PipeWireMetadataProvider sender, PipeWireMetadataEntry entry)
        {
            if (holds(sender.Get(key)))
            {
                sender.EntryChanged -= OnChange;
                reached.TrySetResult();
            }
        }

        provider.EntryChanged += OnChange;
        OnChange(provider, new PipeWireMetadataEntry(0, key, null, null));
        return reached.Task;
    }

    /// <summary>
    /// Binds a store once it reaches the registry, or null when it does not within
    /// <paramref name="limit"/>.
    /// </summary>
    public static async Task<PipeWireMetadataProxy?> BindMetadataWhenPresentAsync(
        this PipeWireRegistry registry,
        string name,
        TimeSpan limit,
        CancellationToken cancellationToken
    ) =>
        await registry
            .WaitForAsync(g => g.GetMetadata(name), cancellationToken)
            .OrDefaultWithin(limit, cancellationToken)
            .ConfigureAwait(false)
            is { } store
            ? registry.BindMetadata(store.Id)
            : null;

    /// <summary>
    /// The result of a wait that may not come, for something optional: null when it does not arrive
    /// within <paramref name="limit"/>.
    /// </summary>
    public static async Task<T?> OrDefaultWithin<T>(
        this Task<T> wait,
        TimeSpan limit,
        CancellationToken cancellationToken
    )
        where T : class
    {
        try
        {
            return await wait.WaitAsync(limit, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Completes once <paramref name="condition"/> holds: checked now and after each frame the
    /// capture delivers, after the handlers subscribed before this one have run.
    /// </summary>
    public static Task Until(this PipeWireVideoCapture capture, Func<bool> condition)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(PipeWireVideoCapture sender, VideoFrame frame)
        {
            if (condition())
            {
                sender.FrameReady -= OnFrame;
                reached.TrySetResult();
            }
        }

        capture.FrameReady += OnFrame;
        if (condition())
        {
            capture.FrameReady -= OnFrame;
            reached.TrySetResult();
        }

        return reached.Task;
    }

    /// <summary>
    /// Reads a value of a bound object until it settles, reading again each time the object reports
    /// a change: a subscribed parameter, or a re-description. A write returns once the daemon has
    /// processed it, not once the object has applied it, so a single read races the object.
    /// </summary>
    /// <returns>The last reading, settled or not: the caller asserts on it.</returns>
    public static Task<T> SettleAsync<T>(
        this PipeWireParameterObject target,
        SpaParamType watched,
        Func<CancellationToken, Task<T>> read,
        Func<T, bool> settled,
        TimeSpan limit,
        CancellationToken cancellationToken
    ) => SettleAsync([target], watched, read, settled, limit, cancellationToken);

    /// <summary>
    /// Reads a value over several bound objects until it settles, reading again each time any of
    /// them reports a change.
    /// </summary>
    /// <returns>The last reading, settled or not: the caller asserts on it.</returns>
    public static async Task<T> SettleAsync<T>(
        this IReadOnlyList<PipeWireParameterObject> targets,
        SpaParamType watched,
        Func<CancellationToken, Task<T>> read,
        Func<T, bool> settled,
        TimeSpan limit,
        CancellationToken cancellationToken
    )
    {
        using var changed = new SemaphoreSlim(0);
        void OnParameter(PipeWireParameterObject sender, SpaObject value) => changed.Release();
        void OnInfo(PipeWireParameterObject sender) => changed.Release();
        var before = new ImmutableArray<SpaParamType>[targets.Count];
        for (int i = 0; i < targets.Count; i++)
        {
            before[i] = targets[i].SubscribedParameters;
            targets[i].ParameterChanged += OnParameter;
            targets[i].InfoChanged += OnInfo;
            targets[i].SubscribeParameters([.. before[i], watched]);
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(limit);
            T last = await read(cancellationToken).ConfigureAwait(false);
            while (!settled(last))
            {
                try
                {
                    await changed.WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Out of time: the last reading is what the caller asserts on.
                    break;
                }

                last = await read(cancellationToken).ConfigureAwait(false);
            }

            return last;
        }
        finally
        {
            for (int i = 0; i < targets.Count; i++)
            {
                targets[i].ParameterChanged -= OnParameter;
                targets[i].InfoChanged -= OnInfo;
                targets[i].SubscribeParameters(before[i].AsSpan());
            }
        }
    }

    /// <summary>
    /// Takes a borrowed frame backed by a descriptor, frame by frame as they arrive, for up to
    /// <paramref name="limit"/>.
    /// </summary>
    public static async Task<(bool Got, BorrowedVideoFrame Frame)> TakeFdBackedAsync(
        this PipeWireVideoCapture capture,
        TimeSpan limit,
        CancellationToken cancellationToken
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limit);
        while (true)
        {
            Task next = capture.NextFrame();
            if (capture.TryGetBorrowedFrame(out BorrowedVideoFrame frame) && frame.IsFdBacked)
                return (true, frame);
            try
            {
                await next.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (false, default);
            }
        }
    }

    /// <inheritdoc cref="NextFrame(PipeWireVideoCapture)"/>
    public static Task NextFrame(this PipeWireAudioCapture capture)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(PipeWireAudioCapture sender, AudioFrame frame)
        {
            sender.FrameReady -= OnFrame;
            arrived.TrySetResult();
        }

        capture.FrameReady += OnFrame;
        return arrived.Task;
    }

    /// <summary>
    /// Runs a cancellable wait for at most <paramref name="limit"/> and carries on either way.
    /// </summary>
    /// <returns>Whether it completed in time.</returns>
    public static async Task<bool> Within(
        Func<CancellationToken, Task> wait,
        TimeSpan limit,
        CancellationToken cancellationToken
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limit);
        try
        {
            await wait(deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Completes once <paramref name="count"/> items have been added and the first and last of
    /// them differ in time: a burst delivered in one cycle reaches a count while spanning none.
    /// </summary>
    public static async Task Spanning<T>(
        this Collected<T> items,
        int count,
        Func<T, long> time,
        CancellationToken cancellationToken
    )
    {
        for (int n = count; ; n++)
        {
            await items.Reaching(n).WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (items)
            {
                if (time(items[^1]) > time(items[0]))
                    return;
            }
        }
    }

    /// <summary>
    /// Waits for a signal for at most <paramref name="limit"/> and carries on either way: the
    /// assertions after it say what was missing.
    /// </summary>
    /// <returns>Whether the signal came.</returns>
    public static async Task<bool> Within(
        this Task signal,
        TimeSpan limit,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await signal.WaitAsync(limit, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}

/// <summary>
/// A count raised from callbacks and awaited without polling: <see cref="AtLeast"/> completes when
/// the count reaches a target.
/// </summary>
internal sealed class Tally
{
    private readonly Lock _gate = new();
    private readonly List<(int Target, TaskCompletionSource Reached)> _waiters = [];
    private int _value;

    public int Value => Volatile.Read(ref _value);

    public static implicit operator int(Tally tally) => tally.Value;

    public override string ToString() =>
        Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public int Increment() => Add(1);

    public int Add(int amount)
    {
        int value = Interlocked.Add(ref _value, amount);
        lock (_gate)
        {
            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                if (value >= _waiters[i].Target)
                {
                    _waiters[i].Reached.TrySetResult();
                    _waiters.RemoveAt(i);
                }
            }
        }

        return value;
    }

    public Task AtLeast(int target)
    {
        lock (_gate)
        {
            if (Value >= target)
                return Task.CompletedTask;
            var reached = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _waiters.Add((target, reached));
            return reached.Task;
        }
    }
}

/// <summary>
/// A list filled from callbacks, under whatever lock they take, whose growth tests await: each
/// <see cref="Add"/> advances a count that <see cref="Reaching"/> waits on.
/// </summary>
internal sealed class Collected<T> : List<T>
{
    private readonly Tally _added = new();

    public new void Add(T item)
    {
        base.Add(item);
        _ = _added.Increment();
    }

    /// <summary>Completes once <paramref name="count"/> items have been added.</summary>
    public Task Reaching(int count) => _added.AtLeast(count);
}
