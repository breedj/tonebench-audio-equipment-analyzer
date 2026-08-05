using System.Buffers;
using System.Numerics;
using Qa403.Device;
using Qa403.Dsp;
using Qa403.Dsp.Fft;

namespace Qa403.App.Streaming;

/// <summary>
/// PcmBlock -&gt; window -&gt; FFT -&gt; PSD/amplitude -&gt; harmonics -&gt; log decimation -&gt; frame -&gt; broadcast.
///
/// Two independent rates are deliberately decoupled:
/// <list type="bullet">
/// <item><see cref="Enqueue"/> runs on the acquisition drain loop and only appends samples into
/// per-channel ring buffers — an O(block size) memcpy-like loop, always faster than real-time,
/// so it can never fall behind or stall acquisition.</item>
/// <item>A separate worker, paced by a <see cref="PeriodicTimer"/> at <see cref="SpectrumPipelineOptions.TargetFrameRateHz"/>,
/// snapshots the latest FFT-sized window and does the actual DSP + broadcast work. If that work
/// takes longer than one tick period, the timer does not queue the missed tick — the next
/// <c>WaitForNextTickAsync</c> simply returns at its own scheduled time. This is what keeps the
/// display rate roughly constant "regardless of FFT size": a huge FFT just means the same-ish
/// window gets re-analyzed across a couple of ticks until enough new samples slide in.</item>
/// </list>
/// </summary>
public sealed class SpectrumPipeline(SpectrumBroadcastHub hub, ILogger<SpectrumPipeline> logger)
{
    private const int MaxChannels = 2;

    private readonly IFftBackend _fft = new MathNetBackend();
    private readonly SpectrumAverager[] _ampAveragers = [new(), new()];
    private readonly SpectrumAverager[] _psdAveragers = [new(), new()];
    private readonly object _ringBufferLock = new();

    private SampleRingBuffer?[] _ringBuffers = new SampleRingBuffer?[MaxChannels];
    private int _ringBufferFftSize;
    private double _sampleRateHz;
    private int _channelCount;
    private uint _seq;
    private CancellationTokenSource? _cts;
    private Task? _worker;

    private volatile SpectrumPipelineOptions _options = new();

    public SpectrumPipelineOptions Options
    {
        get => _options;
        set => _options = value ?? throw new ArgumentNullException(nameof(value));
    }

    public void Start(double sampleRateHz, int channelCount)
    {
        _sampleRateHz = sampleRateHz;
        _channelCount = Math.Clamp(channelCount, 1, MaxChannels);
        _seq = 0;
        EnsureRingBuffers(_options.FftSize);

        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var worker = _worker;
        _cts = null;
        _worker = null;

        if (cts is null)
            return;

        cts.Cancel();
        try
        {
            if (worker is not null)
                await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <summary>
    /// Feeds one acquisition block's samples into the per-channel ring buffers. Takes ownership
    /// of <paramref name="block"/> — disposes it before returning, per <c>PcmBlock</c>'s contract.
    /// </summary>
    public void Enqueue(PcmBlock block)
    {
        using (block)
        {
            EnsureRingBuffers(_options.FftSize);

            var ringBuffers = _ringBuffers;
            int channelCount = Math.Min(block.ChannelCount, MaxChannels);
            for (int ch = 0; ch < channelCount; ch++)
                ringBuffers[ch]?.AppendInterleaved(block.Samples, ch, block.ChannelCount);
        }
    }

    private void EnsureRingBuffers(int fftSize)
    {
        if (_ringBufferFftSize == fftSize)
            return;

        lock (_ringBufferLock)
        {
            if (_ringBufferFftSize == fftSize)
                return;

            var next = new SampleRingBuffer?[MaxChannels];
            for (int ch = 0; ch < MaxChannels; ch++)
                next[ch] = new SampleRingBuffer(fftSize);

            _ringBuffers = next;
            _ringBufferFftSize = fftSize;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // Re-read the period every iteration so a live TargetFrameRateHz change takes effect
            // without restarting the pipeline.
            while (!ct.IsCancellationRequested)
            {
                var period = TimeSpan.FromSeconds(1.0 / Math.Max(1, _options.TargetFrameRateHz));
                using var timer = new PeriodicTimer(period);
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    break;

                try
                {
                    ProcessTick();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Spectrum pipeline tick failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop.
        }
    }

    private void ProcessTick()
    {
        var options = _options;
        var ringBuffers = _ringBuffers;

        for (int ch = 0; ch < _channelCount; ch++)
        {
            bool enabled = ch == 0 ? options.ProcessLeft : options.ProcessRight;
            if (!enabled)
                continue;

            var rb = ringBuffers[ch];
            if (rb is null || rb.Capacity != options.FftSize || !rb.IsFull)
                continue;

            ProcessChannel(ch, rb, options);
        }
    }

    private void ProcessChannel(int channelIndex, SampleRingBuffer rb, SpectrumPipelineOptions options)
    {
        int n = options.FftSize;
        int specLen = (n / 2) + 1;

        double[] timeBuf = ArrayPool<double>.Shared.Rent(n);
        double[] windowed = ArrayPool<double>.Shared.Rent(n);
        Complex[] spec = ArrayPool<Complex>.Shared.Rent(specLen);
        double[] ampBuf = ArrayPool<double>.Shared.Rent(specLen);
        double[] psdBuf = ArrayPool<double>.Shared.Rent(specLen);
        try
        {
            rb.SnapshotLatest(timeBuf.AsSpan(0, n));

            var window = Window.Get(options.Window, n);
            Window.Apply(timeBuf.AsSpan(0, n), window, windowed.AsSpan(0, n));

            var specSpan = spec.AsSpan(0, specLen);
            _fft.Forward(windowed.AsSpan(0, n), specSpan);

            var amp = AmplitudeSpectrum.Compute(specSpan, window.Sums, ampBuf.AsSpan(0, specLen));
            var psdInstant = PsdSpectrum.Compute(specSpan, _sampleRateHz, window.Sums, psdBuf.AsSpan(0, specLen));
            double binSizeHz = psdInstant.BinSizeHz;

            // Average in power domain: square the amplitude-domain trace before averaging (never
            // average dBV directly, and never average across the two different normalisations).
            Span<double> ampPower = ampBuf.AsSpan(0, specLen);
            for (int i = 0; i < specLen; i++)
                ampPower[i] *= ampPower[i];
            _ampAveragers[channelIndex].Update(ampPower, options.Averaging, options.AverageCount);

            Span<double> psdValues = psdBuf.AsSpan(0, specLen);
            _psdAveragers[channelIndex].Update(psdValues, options.Averaging, options.AverageCount);
            var psd = PsdSpectrum.FromValues(psdValues, _sampleRateHz, binSizeHz, window.Sums);

            uint seq = Interlocked.Increment(ref _seq);
            var channel = channelIndex == 0 ? StreamChannel.Left : StreamChannel.Right;

            (float peakDbv, float totalRmsDbv) = ComputeTimeDomainLevels(timeBuf.AsSpan(0, n));

            BroadcastSpectrumFrame(channel, seq, ampPower, binSizeHz, options);
            BroadcastMetersFrame(channel, seq, psd, peakDbv, totalRmsDbv, options);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(timeBuf);
            ArrayPool<double>.Shared.Return(windowed);
            ArrayPool<Complex>.Shared.Return(spec);
            ArrayPool<double>.Shared.Return(ampBuf);
            ArrayPool<double>.Shared.Return(psdBuf);
        }
    }

    private void BroadcastSpectrumFrame(StreamChannel channel, uint seq, ReadOnlySpan<double> ampPower, double binSizeHz, SpectrumPipelineOptions options)
    {
        int specLen = ampPower.Length;
        int loBin = Math.Clamp((int)Math.Round(options.DisplayLoFreqHz / binSizeHz), 1, specLen - 2);

        var columns = new MinMax[options.DisplayColumns];
        var centerBins = new double[options.DisplayColumns];
        // DecimateLogWithCenters (not the plain DecimateLog + a client-side reconstruction
        // formula) because the column-to-bin mapping is only a smooth log curve when there are
        // many more source bins than columns; for a small FFT decimated to ~1600 columns, most
        // low-frequency columns collapse to single adjacent bins and a formula-based frequency
        // would be wrong by orders of magnitude. The actual center bin this call used is the only
        // thing safe to send as "this column's frequency".
        SpectrumDecimator.DecimateLogWithCenters(ampPower, columns, centerBins, loBin);

        var columnFreqsHz = new float[options.DisplayColumns];

        // ampPower is voltage^2; dBV = 20*log10(sqrt(power)) = 10*log10(power). Converting only
        // the ~2*DisplayColumns decimated values, not all specLen bins, is why decimation runs
        // before this conversion rather than after.
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i] = new MinMax(
                10.0 * Math.Log10(Math.Max(columns[i].Min, double.Epsilon)),
                10.0 * Math.Log10(Math.Max(columns[i].Max, double.Epsilon)));
            columnFreqsHz[i] = (float)(centerBins[i] * binSizeHz);
        }

        float loFreqHz = (float)(loBin * binSizeHz);
        float hiFreqHz = (float)((specLen - 1) * binSizeHz);
        byte[] frame = StreamFrame.EncodeSpectrum(channel, seq, loFreqHz, hiFreqHz, columnFreqsHz, columns, StreamMsgType.Spectrum, logSpaced: true);
        hub.Broadcast(frame);
    }

    /// <summary>
    /// True instantaneous peak and broadband RMS from the raw (unwindowed) time-domain snapshot —
    /// not spectral estimates. Peak is expressed as its RMS-equivalent (peakVpk/sqrt2), matching
    /// every other dBV value in this app's convention (see <c>SignalSynthesizer.DbvToPeakV</c>).
    /// </summary>
    private static (float PeakDbv, float TotalRmsDbv) ComputeTimeDomainLevels(ReadOnlySpan<double> timeDomainSamples)
    {
        double peakVpk = 0.0;
        double sumSquares = 0.0;
        foreach (double v in timeDomainSamples)
        {
            double a = Math.Abs(v);
            if (a > peakVpk)
                peakVpk = a;
            sumSquares += v * v;
        }

        double peakVrms = peakVpk / Math.Sqrt(2.0);
        double totalRmsVrms = Math.Sqrt(sumSquares / timeDomainSamples.Length);

        float peakDbv = (float)(20.0 * Math.Log10(Math.Max(peakVrms, double.Epsilon)));
        float totalRmsDbv = (float)(20.0 * Math.Log10(Math.Max(totalRmsVrms, double.Epsilon)));
        return (peakDbv, totalRmsDbv);
    }

    private void BroadcastMetersFrame(
        StreamChannel channel, uint seq, PsdSpectrum psd, float peakDbv, float totalRmsDbv, SpectrumPipelineOptions options)
    {
        int minSearchBin = Math.Max(1, (int)Math.Round(options.FundamentalMinSearchHz / psd.BinSizeHz));
        int fundamentalBin = FindGlobalPeakBin(psd.ValuesV2PerHz, minSearchBin);
        if (fundamentalBin <= 0)
            return;

        int harmonicCount = Math.Max(1, options.HarmonicCount);
        Span<Harmonic> harmonics = harmonicCount <= 32 ? stackalloc Harmonic[harmonicCount] : new Harmonic[harmonicCount];
        int written = HarmonicAnalyzer.Analyze(psd, fundamentalBin, harmonicCount, harmonics);
        if (written == 0)
            return;

        harmonics = harmonics[..written];
        var fundamental = harmonics[0];

        float fundamentalFreqHz = (float)(fundamentalBin * psd.BinSizeHz);
        float thdPercent = (float)(SpectrumMetrics.Thd(harmonics) * 100.0);
        float thdNPercent = (float)(SpectrumMetrics.ThdPlusN(psd, fundamental) * 100.0);
        float snrDb = (float)SpectrumMetrics.SnrDb(psd, harmonics);
        float fundamentalDbv = (float)fundamental.AmplitudeDbv;
        float noiseFloorDbv = fundamentalDbv - snrDb;

        var otherPeaks = FindOtherPeaks(psd, harmonics, fundamentalDbv, options);

        byte[] frame = StreamFrame.EncodeMeters(
            channel, seq, fundamentalFreqHz, (float)psd.BinSizeHz,
            thdPercent, thdNPercent, snrDb, fundamentalDbv, noiseFloorDbv, peakDbv, totalRmsDbv,
            harmonics, otherPeaks);
        hub.Broadcast(frame);
    }

    /// <summary>
    /// Everything else worth calling out in the spectrum: mains hum, its own harmonics, and real
    /// intermodulation/mixing products between multiple simultaneous generator tones (the shared
    /// soft-clip in SimulatedDevice is a genuine nonlinearity, so these are real products, not
    /// synthesized ones). Scanned from <see cref="SpectrumPipelineOptions.DisplayLoFreqHz"/>, not
    /// <see cref="SpectrumPipelineOptions.FundamentalMinSearchHz"/> — hum at 50/100 Hz is exactly
    /// what this is for.
    /// </summary>
    private static (float FreqHz, float Dbc)[] FindOtherPeaks(
        PsdSpectrum psd, ReadOnlySpan<Harmonic> harmonics, float fundamentalDbv, SpectrumPipelineOptions options)
    {
        int otherPeaksCount = Math.Max(0, options.OtherPeaksCount);
        if (otherPeaksCount == 0)
            return [];

        Span<BinRange> excluded = harmonics.Length <= 32 ? stackalloc BinRange[harmonics.Length] : new BinRange[harmonics.Length];
        for (int i = 0; i < harmonics.Length; i++)
        {
            excluded[i] = new BinRange(
                harmonics[i].Bin - HarmonicAnalyzer.DefaultSkirtHalfWidthBins, harmonics[i].Bin + HarmonicAnalyzer.DefaultSkirtHalfWidthBins);
        }

        int minBin = Math.Max(1, (int)Math.Round(options.DisplayLoFreqHz / psd.BinSizeHz));
        Span<SpectralPeak> peaks = otherPeaksCount <= 32 ? stackalloc SpectralPeak[otherPeaksCount] : new SpectralPeak[otherPeaksCount];
        int written = PeakFinder.FindPeaks(psd, excluded, otherPeaksCount, peaks, minBin);

        var result = new (float FreqHz, float Dbc)[written];
        for (int i = 0; i < written; i++)
            result[i] = ((float)(peaks[i].Bin * psd.BinSizeHz), (float)(peaks[i].AmplitudeDbv - fundamentalDbv));

        return result;
    }

    private static int FindGlobalPeakBin(ReadOnlySpan<double> psdValues, int loBinInclusive)
    {
        if (loBinInclusive >= psdValues.Length)
            return -1;

        int peak = loBinInclusive;
        double peakValue = psdValues[loBinInclusive];
        for (int k = loBinInclusive + 1; k < psdValues.Length; k++)
        {
            if (psdValues[k] > peakValue)
            {
                peakValue = psdValues[k];
                peak = k;
            }
        }

        return peak;
    }
}
