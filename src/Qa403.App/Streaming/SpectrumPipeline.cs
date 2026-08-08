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

        // Unconditionally, because EnsureRingBuffers only allocates fresh buffers when the FFT size
        // changed. Restarting at a new sample rate keeps the same size, so without this the first
        // FFTs after the restart analyze samples captured at the *old* rate — see Flush.
        Flush();

        var cts = new CancellationTokenSource();
        _cts = cts;

        // Capture the token by value into a local, not the field, before handing it to Task.Run: the
        // lambda doesn't actually run until the thread pool gets to it, and if StopAsync races in first
        // and nulls _cts (see below), reading the field lazily inside the lambda throws a
        // NullReferenceException instead of observing an already-cancelled token like it should.
        var runCt = cts.Token;
        _worker = Task.Run(() => RunAsync(runCt));
    }

    /// <summary>
    /// Drops every buffered sample and all averaging state, so the next broadcast frame is built
    /// only from samples captured after this call.
    ///
    /// Call this whenever the signal being analyzed changes discontinuously: a new sample rate, a
    /// generator change, an input-range change. An FFT record that straddles such a change carries
    /// a step, and a step smears the tone across a broad skirt — the "noise floor jumps up and then
    /// settles" artifact, which lasts exactly one buffer-fill. Flushing trades that for a brief
    /// pause in updates (the UI keeps showing the last good frame) while a clean record fills.
    /// </summary>
    public void Flush()
    {
        var ringBuffers = _ringBuffers;
        for (int ch = 0; ch < MaxChannels; ch++)
        {
            ringBuffers[ch]?.Reset();
            _ampAveragers[ch].Reset();
            _psdAveragers[ch].Reset();
        }
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

    /// <summary>
    /// How much narrower than the full span a client's zoom window has to be before it is worth
    /// encoding a separate, denser frame for it.
    ///
    /// Measured on the log axis, because that is what the column budget is spread over: a window
    /// covering 1/4 of the span's decades receives 1/4 of the overview's columns, so a dedicated
    /// frame gives it 4x the resolution. Below this the gain does not justify a second decimation
    /// and encode per tick per distinct window — and the overwhelmingly common case (nobody
    /// zoomed) then costs exactly what it did before this existed: one decimation, one broadcast.
    /// </summary>
    private const double ZoomedFrameMinFactor = 4.0;

    private void BroadcastSpectrumFrame(StreamChannel channel, uint seq, ReadOnlySpan<double> ampPower, double binSizeHz, SpectrumPipelineOptions options)
    {
        var clients = hub.ClientViewports();
        if (clients.Length == 0)
            return;

        int specLen = ampPower.Length;
        int overviewLoBin = Math.Clamp((int)Math.Round(options.DisplayLoFreqHz / binSizeHz), 1, specLen - 2);
        int overviewHiBin = specLen - 1;
        double fullDecades = LogSpan(overviewLoBin, overviewHiBin);

        // Group the zoomed clients by window so N clients looking at the same range cost one
        // encode, not N. Everyone else shares the single overview frame.
        Dictionary<SpectrumViewport, List<Guid>>? zoomed = null;
        List<Guid>? overview = null;

        foreach (var (id, viewport) in clients)
        {
            if (TryResolveZoom(viewport, binSizeHz, specLen, overviewLoBin, fullDecades, out _, out _))
            {
                zoomed ??= [];
                if (!zoomed.TryGetValue(viewport!, out var ids))
                    zoomed[viewport!] = ids = [];
                ids.Add(id);
            }
            else
            {
                (overview ??= []).Add(id);
            }
        }

        if (overview is not null)
        {
            byte[] frame = BuildSpectrumFrame(
                channel, seq, ampPower, binSizeHz, overviewLoBin, overviewHiBin, options.DisplayColumns, zoomed: false);
            foreach (var id in overview)
                hub.Send(id, frame);
        }

        if (zoomed is null)
            return;

        foreach (var (viewport, ids) in zoomed)
        {
            TryResolveZoom(viewport, binSizeHz, specLen, overviewLoBin, fullDecades, out int loBin, out int hiBin);
            byte[] frame = BuildSpectrumFrame(
                channel, seq, ampPower, binSizeHz, loBin, hiBin, options.DisplayColumns, zoomed: true);
            foreach (var id in ids)
                hub.Send(id, frame);
        }
    }

    private static double LogSpan(int loBin, int hiBin) =>
        loBin >= 1 && hiBin > loBin ? Math.Log((double)hiBin / loBin) : 0.0;

    /// <summary>
    /// Turns a client's requested window into a bin range, and decides whether it is zoomed in far
    /// enough to earn its own frame. Returns false (and leaves the bins unset) for no viewport, a
    /// degenerate one, or anything under <see cref="ZoomedFrameMinFactor"/>.
    /// </summary>
    private static bool TryResolveZoom(
        SpectrumViewport? viewport, double binSizeHz, int specLen, int overviewLoBin, double fullDecades,
        out int loBin, out int hiBin)
    {
        loBin = 0;
        hiBin = 0;

        if (viewport is null || fullDecades <= 0.0)
            return false;

        loBin = Math.Clamp((int)Math.Floor(viewport.LoFreqHz / binSizeHz), overviewLoBin, specLen - 2);
        hiBin = Math.Clamp((int)Math.Ceiling(viewport.HiFreqHz / binSizeHz), loBin + 1, specLen - 1);

        return LogSpan(loBin, hiBin) * ZoomedFrameMinFactor <= fullDecades;
    }

    private static byte[] BuildSpectrumFrame(
        StreamChannel channel, uint seq, ReadOnlySpan<double> ampPower, double binSizeHz,
        int loBin, int hiBin, int maxColumns, bool zoomed)
    {
        // Never more columns than there are bins to put in them: past that point extra columns just
        // repeat single bins and cost bandwidth for nothing.
        int columnCount = Math.Clamp(hiBin - loBin + 1, 1, maxColumns);

        var columns = new MinMax[columnCount];
        var centerBins = new double[columnCount];
        // DecimateLogWithCenters (not the plain DecimateLog + a client-side reconstruction
        // formula) because the column-to-bin mapping is only a smooth log curve when there are
        // many more source bins than columns; for a small FFT decimated to ~1600 columns, most
        // low-frequency columns collapse to single adjacent bins and a formula-based frequency
        // would be wrong by orders of magnitude. The actual center bin this call used is the only
        // thing safe to send as "this column's frequency".
        //
        // Slicing at hiBin is what makes a zoomed frame work: the decimator spreads its columns
        // across [loBin, end-of-span], so a shorter span is a denser one for the same budget.
        SpectrumDecimator.DecimateLogWithCenters(ampPower[..(hiBin + 1)], columns, centerBins, loBin);

        var columnFreqsHz = new float[columnCount];

        // ampPower is the square of AmplitudeSpectrum.PeakVoltsPerBin, i.e. volts *peak* squared —
        // so dBV = 10*log10(power / 2), the /2 being (Vpk/sqrt2)^2. Dropping it put every value on
        // this chart, trace and noise floor alike, exactly 3.01 dB high, and disagreeing by that
        // much with the meters frame (which reaches dBV through PsdSpectrum/HarmonicAnalyzer and
        // was the only path the tests checked). See AmplitudeSpectrum.VrmsAt/DbvAt, and the
        // golden-test convention in CLAUDE.md: 1 kHz at 1.0 V peak, Hann, reads -3.01 dBV.
        //
        // Converting only the ~2*columnCount decimated values, not all specLen bins, is why
        // decimation runs before this conversion rather than after.
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i] = new MinMax(
                10.0 * Math.Log10(Math.Max(columns[i].Min / 2.0, double.Epsilon)),
                10.0 * Math.Log10(Math.Max(columns[i].Max / 2.0, double.Epsilon)));
            columnFreqsHz[i] = (float)(centerBins[i] * binSizeHz);
        }

        return StreamFrame.EncodeSpectrum(
            channel, seq, (float)(loBin * binSizeHz), (float)(hiBin * binSizeHz),
            columnFreqsHz, columns, StreamMsgType.Spectrum, logSpaced: true, zoomed: zoomed);
    }

    /// <summary>
    /// True instantaneous peak and broadband RMS from the raw (unwindowed) time-domain snapshot —
    /// not spectral estimates. Peak is expressed as its RMS-equivalent (peakVpk/sqrt2), matching
    /// every other dBV value in this app's convention (see <c>SignalSynthesizer.DbvToPeakV</c>).
    ///
    /// The DC component is removed first, for the same reason the noise integrations exclude bin 0:
    /// a DC-coupled input's offset is not part of the signal being measured. Leaving it in added
    /// itself straight onto the peak — a 4.6 mV offset read as +0.03 dBV on a 1 V tone, which is
    /// why Pk Vrms sat above 1.0000 for a 1.0000 Vrms generator.
    /// </summary>
    private static (float PeakDbv, float TotalRmsDbv) ComputeTimeDomainLevels(ReadOnlySpan<double> timeDomainSamples)
    {
        double mean = 0.0;
        foreach (double v in timeDomainSamples)
            mean += v;
        mean /= timeDomainSamples.Length;

        double peakVpk = 0.0;
        double sumSquares = 0.0;
        foreach (double sample in timeDomainSamples)
        {
            double v = sample - mean;
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

        // The measurement bandwidth for every noise-integrated figure below, and the upper bound
        // on which harmonics count toward THD. See SpectrumPipelineOptions.MeasurementLoFreqHz —
        // passing this rather than the full spectrum is what keeps DC and out-of-band noise out
        // of THD+N/SNR.
        var band = psd.BandOf(options.MeasurementLoFreqHz, options.MeasurementHiFreqHz);

        int harmonicCount = Math.Max(1, options.HarmonicCount);
        Span<Harmonic> harmonics = harmonicCount <= 32 ? stackalloc Harmonic[harmonicCount] : new Harmonic[harmonicCount];
        int written = HarmonicAnalyzer.Analyze(
            psd, fundamentalBin, harmonicCount, harmonics, maxBinInclusive: band.HiBinInclusive);
        if (written == 0)
            return;

        harmonics = harmonics[..written];
        var fundamental = harmonics[0];

        float fundamentalFreqHz = (float)(fundamentalBin * psd.BinSizeHz);
        float thdPercent = (float)(SpectrumMetrics.Thd(harmonics) * 100.0);
        float thdNPercent = (float)(SpectrumMetrics.ThdPlusN(psd, fundamental, band, options.NotchOctaves) * 100.0);
        float snrDb = (float)SpectrumMetrics.SnrDb(psd, harmonics, band, options.NotchOctaves);
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
