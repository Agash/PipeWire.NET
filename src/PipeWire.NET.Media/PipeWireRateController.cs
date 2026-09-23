namespace PipeWire.NET.Media;

/// <summary>
/// Turns "my queue is the wrong length" into a rate correction, smoothly.
/// </summary>
/// <remarks>
/// Anything bridging the graph clock to a clock this library does not own - a network transport,
/// another device - has two clocks that will not agree for long. Left alone, the queue between them
/// drifts until it either starves or overruns. The fix is not to jump the rate whenever the queue
/// is wrong, which oscillates; it is to feed the error through a filter that converges.
/// <para>
/// This is a managed port of PipeWire's <c>spa_dll</c> (<c>spa/utils/dll.h</c>), the same filter its
/// own rtp and tunnel modules use for exactly this. The arithmetic is theirs, kept identical so a
/// correction computed here matches one computed there.
/// </para>
/// <para>
/// Feed <see cref="Update"/> the queue error each cycle and pass the result to a stream's
/// <c>SetRate</c>. Reset with <see cref="SetBandwidth"/> when the error is so large that the filter
/// would take too long to catch up - a reconnect, a seek - rather than letting it converge slowly.
/// </para>
/// </remarks>
public sealed class PipeWireRateController
{
    /// <summary>The widest bandwidth upstream uses; converges fastest, follows noise most.</summary>
    public const double MaxBandwidth = 0.128;

    /// <summary>The narrowest bandwidth upstream uses; steadiest, slowest to converge.</summary>
    public const double MinBandwidth = 0.016;

    private double _z1, _z2, _z3;
    private double _w0, _w1, _w2;

    /// <summary>The bandwidth currently in effect.</summary>
    public double Bandwidth { get; private set; }

    /// <summary>
    /// Sets how quickly corrections respond, and clears the filter's history.
    /// </summary>
    /// <param name="bandwidth">
    /// Between <see cref="MinBandwidth"/> and <see cref="MaxBandwidth"/>. Start narrow; widen only
    /// if convergence is genuinely too slow.
    /// </param>
    /// <param name="period">Frames per cycle - the quantum.</param>
    /// <param name="rate">Sample rate in Hz.</param>
    /// <exception cref="ArgumentOutOfRangeException">A period or rate of zero has no meaning here.</exception>
    public void SetBandwidth(double bandwidth, int period, int rate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(rate, 0);

        double w = 2.0 * Math.PI * bandwidth * period / rate;
        _w0 = 1.0 - Math.Exp(-20.0 * w);
        _w1 = w * 1.5 / period;
        _w2 = w / 1.5;
        Bandwidth = bandwidth;
    }

    /// <summary>Clears the filter's history without changing its bandwidth.</summary>
    public void Reset() => _z1 = _z2 = _z3 = 0.0;

    /// <summary>
    /// Folds one cycle's error into the filter and returns the correction to apply.
    /// </summary>
    /// <param name="error">
    /// How far the queue is from where it should be, in frames: positive when it holds more than
    /// wanted. Clamp it before calling - a single outlier that reaches the filter takes many cycles
    /// to leave it.
    /// </param>
    /// <returns>
    /// A multiplier around 1.0. PipeWire's transports pass <c>1.0 / correction</c> to
    /// <c>SetRate</c>; which way round depends on whether the stream is producing or consuming, so
    /// check against a queue that is deliberately too long before trusting the sign.
    /// </returns>
    public double Update(double error)
    {
        _z1 += _w0 * ((_w1 * error) - _z1);
        _z2 += _w0 * (_z1 - _z2);
        _z3 += _w2 * _z2;
        return 1.0 - (_z2 + _z3);
    }
}
