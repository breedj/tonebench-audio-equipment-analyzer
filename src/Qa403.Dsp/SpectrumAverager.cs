namespace Qa403.Dsp;

/// <summary>
/// Averages successive power-domain spectra (PSD V²/Hz, or amplitude-squared V² — any linear,
/// power-like quantity; never average dB values or PSD/amplitude directly-in-voltage, both of
/// which would bias the mean). One instance holds the running state for one continuous stream
/// (e.g. one channel) — reset by calling <see cref="Update"/> with <see cref="AveragingMode.Off"/>.
///
/// Linear mode keeps <c>count</c> full previous spectra (memory scales with count × spectrum
/// length — keep count modest for very large FFTs). Exponential mode keeps only the running EMA,
/// so its memory cost is one spectrum regardless of count.
/// </summary>
public sealed class SpectrumAverager
{
    private double[]? _ema;
    private double[][]? _history;
    private int _historyCount;
    private int _historyNext;

    /// <summary>
    /// Drops the accumulated average, so the next <see cref="Update"/> starts a fresh window.
    /// Needed whenever the signal changes discontinuously (new sample rate, new stimulus): the
    /// spectra averaged so far describe the old signal and would otherwise blend into the new one.
    /// </summary>
    public void Reset()
    {
        _ema = null;
        _history = null;
        _historyCount = 0;
        _historyNext = 0;
    }

    /// <summary>
    /// Replaces <paramref name="current"/> in place with the running average of itself and
    /// previous calls. With <see cref="AveragingMode.Off"/> or <c>count &lt;= 1</c>,
    /// <paramref name="current"/> is left untouched and internal state is cleared (so switching
    /// back to averaging later starts a fresh window rather than resuming a stale one).
    /// </summary>
    public void Update(Span<double> current, AveragingMode mode, int count)
    {
        if (mode == AveragingMode.Off || count <= 1)
        {
            _ema = null;
            _history = null;
            return;
        }

        if (mode == AveragingMode.Exponential)
        {
            UpdateExponential(current, count);
            return;
        }

        UpdateLinear(current, count);
    }

    private void UpdateExponential(Span<double> current, int count)
    {
        if (_ema is null || _ema.Length != current.Length)
        {
            _ema = current.ToArray();
            return;
        }

        double alpha = 2.0 / (count + 1);
        for (int i = 0; i < current.Length; i++)
            _ema[i] += alpha * (current[i] - _ema[i]);

        _ema.CopyTo(current);
    }

    private void UpdateLinear(Span<double> current, int count)
    {
        if (_history is null || _history.Length != count || _history[0].Length != current.Length)
        {
            _history = new double[count][];
            for (int i = 0; i < count; i++)
                _history[i] = new double[current.Length];
            _historyCount = 0;
            _historyNext = 0;
        }

        current.CopyTo(_history[_historyNext]);
        _historyNext = (_historyNext + 1) % count;
        _historyCount = Math.Min(_historyCount + 1, count);

        for (int i = 0; i < current.Length; i++)
        {
            double sum = 0.0;
            for (int h = 0; h < _historyCount; h++)
                sum += _history[h][i];
            current[i] = sum / _historyCount;
        }
    }
}
