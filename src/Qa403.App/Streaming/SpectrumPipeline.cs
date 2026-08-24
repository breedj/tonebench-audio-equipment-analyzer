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

    // Retained across StopAsync (so a stopped acquisition can still be saved) and cleared only by
    // Flush() (so restarting acquisition drops the stale trace). Holds ampPower -- peak-volts
    // squared, the same pre-dBV quantity ProcessChannel already computes -- rather than the
    // converted dBV trace: a Math.Log10 pass over every bin only needs to happen when someone
    // actually calls LatestSnapshots() (a user-initiated save), not on every one of this tick's
    // ~20/sec iterations regardless of whether a save ever happens. See LatestSnapshots.
    private readonly RetainedSpectrum[] _retained = [new(), new()];
    private readonly object _retainedLock = new();

    // Set once a channel's SpectrumPipelineOptions.SingleShot capture completes; ProcessTick then
    // skips that channel entirely, which is what "holds" the last broadcast frame on screen. Only
    // Flush() clears it, so toggling SingleShot off and back on without a Flush would otherwise
    // resume immediately held from a previous capture rather than starting a fresh window.
    private readonly bool[] _singleShotHeld = new bool[MaxChannels];

    // SampleRingBuffer.SamplesWritten as it stood when each channel last committed an average, or 0
    // for "no average yet in this epoch". Drives SpectrumPipelineOptions.AverageHopFraction; see
    // HasFreshSignalForAverage. Reset by Flush and by a ring-buffer resize, both of which restart
    // the counter it is compared against.
    private readonly long[] _lastAverageWritten = new long[MaxChannels];

    /// <summary>
    /// Bumped by every <see cref="Flush"/>. A tick reads it before it starts and again before it
    /// commits anything, and throws its work away if the two differ — see <see cref="ProcessChannel"/>.
    /// </summary>
    private long _flushGeneration;

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
        set
        {
            _options = value ?? throw new ArgumentNullException(nameof(value));

            // Resize now, on the caller's thread, rather than leaving it to the next Enqueue on the
            // acquisition thread. A caller that changes FftSize and then flushes -- the sweep does
            // exactly that, per point -- otherwise races the two: Flush captures _ringBuffers and
            // books the stimulus-latency discard on it while EnsureRingBuffers concurrently swaps in
            // replacements that read DiscardRemaining before the discard was set. The discard is
            // then lost and the first record after the change refills with pre-change audio, which
            // reads as a fundamental several dB low on the first point at each new FFT size.
            //
            // Guarded on the pipeline having started: before Start() there are no buffers to keep in
            // step, and Start() does its own EnsureRingBuffers.
            if (_ringBufferFftSize != 0)
                EnsureRingBuffers(_options.FftSize);
        }
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
    /// only from samples captured after the change that prompted this call actually reached the wire.
    ///
    /// Call this whenever the signal being analyzed changes discontinuously: a new sample rate, a
    /// generator change, an input-range change. An FFT record that straddles such a change carries
    /// a step, and a step smears the tone across a broad skirt — the "noise floor jumps up and then
    /// settles" artifact, which lasts exactly one buffer-fill. Flushing trades that for a brief
    /// pause in updates (the UI keeps showing the last good frame) while a clean record fills.
    ///
    /// <para><paramref name="discardFrames"/> must be
    /// <see cref="Qa403.Device.IAnalyzerDevice.StimulusLatencyFrames"/> for any caller flushing
    /// because of a stimulus change, and 0 only when nothing changed on the wire (an acquisition
    /// restart, where there is no stale audio to outrun). Passing 0 for a stimulus change is the
    /// bug this parameter exists for: the flush lands before the change does, so the buffer refills
    /// with pre-change audio and the step ends up inside the first record anyway — measured at up to
    /// +41 dB of broadband floor on a 1 dB amplitude change. Worse, the contaminated record then
    /// becomes average #1, so with 8 averages it stays on screen for eight frames rather than one.
    /// See <see cref="SampleRingBuffer.Reset"/> for the measurements.</para>
    /// </summary>
    public void Flush(int discardFrames = 0)
    {
        // Before anything else, so a tick already running is guaranteed to see the new value by the
        // time it reaches its commit check.
        Interlocked.Increment(ref _flushGeneration);

        var ringBuffers = _ringBuffers;
        for (int ch = 0; ch < MaxChannels; ch++)
        {
            ringBuffers[ch]?.Reset(discardFrames);
            _ampAveragers[ch].Reset();
            _psdAveragers[ch].Reset();
            Volatile.Write(ref _singleShotHeld[ch], false);
            // Reset() restarts SamplesWritten at 0, so a value carried over from the previous epoch
            // would sit above it and stall the hop gate permanently.
            Volatile.Write(ref _lastAverageWritten[ch], 0);
        }

        lock (_retainedLock)
        {
            for (int ch = 0; ch < MaxChannels; ch++)
                _retained[ch] = new RetainedSpectrum();
        }
    }

    /// <summary>
    /// True once every channel this pipeline is processing has finished its
    /// <see cref="SpectrumPipelineOptions.SingleShot"/> capture and held — the server-side
    /// equivalent of a client watching for <see cref="StreamFlags.Held"/> on the meters frame.
    ///
    /// This is what <c>ThdSweepRunner</c> awaits between sweep points: it needs "the averages for
    /// this frequency are complete" without being a WebSocket client of its own. Always false while
    /// <c>SingleShot</c> is off (a free-running pipeline never completes), and cleared by
    /// <see cref="Flush"/> — so the arming sequence is "set SingleShot, then Flush", and this stays
    /// false until the capture that flush armed is done.
    ///
    /// A channel that is not processed cannot hold and is skipped, matching
    /// <see cref="ProcessTick"/>; if neither channel is processed there is nothing to wait for and
    /// this is false rather than vacuously true, so a misconfigured sweep times out loudly instead
    /// of silently recording empty points.
    /// </summary>
    public bool SingleShotComplete
    {
        get
        {
            var options = _options;
            if (!options.SingleShot)
                return false;

            bool any = false;
            for (int ch = 0; ch < _channelCount; ch++)
            {
                bool enabled = ch == 0 ? options.ProcessLeft : options.ProcessRight;
                if (!enabled)
                    continue;

                any = true;
                if (!Volatile.Read(ref _singleShotHeld[ch]))
                    return false;
            }

            return any;
        }
    }

    /// <summary>
    /// The last full-resolution spectrum retained per channel, for a "save current capture"
    /// action -- deliberately unrelated to the ~1600-column decimated display frames this
    /// pipeline broadcasts. Empty before the first tick or right after <see cref="Flush"/>; a
    /// channel with <see cref="SpectrumPipelineOptions.ProcessLeft"/>/<c>ProcessRight</c> off is
    /// simply absent, same as it is from the live broadcast.
    ///
    /// Gated only on the trace itself (<c>AmpPower</c>), not on <c>Meters</c> -- a tick that found
    /// no fundamental in band still produced a perfectly good spectrum (see
    /// <see cref="SpectrumSnapshot.Meters"/>), and requiring Meters here used to make "Save" fail
    /// with "No spectrum has been captured yet." even while a trace was clearly on screen.
    ///
    /// Deliberately not called on every tick's hot path: it converts the retained peak-volts^2
    /// trace to dBV here, on the caller's thread, rather than in <see cref="ProcessChannel"/> --
    /// see <see cref="_retained"/>.
    /// </summary>
    public SpectrumSnapshot[] LatestSnapshots()
    {
        lock (_retainedLock)
        {
            List<SpectrumSnapshot>? result = null;

            for (int ch = 0; ch < MaxChannels; ch++)
            {
                var retained = _retained[ch];
                if (retained.AmpPower is null)
                    continue;

                var dbv = new double[retained.AmpPower.Length];
                for (int i = 0; i < dbv.Length; i++)
                    dbv[i] = 10.0 * Math.Log10(Math.Max(retained.AmpPower[i] / 2.0, double.Epsilon));

                (result ??= []).Add(new SpectrumSnapshot(
                    ch == 0 ? StreamChannel.Left : StreamChannel.Right, retained.Seq, retained.CapturedUtc,
                    retained.SampleRateHz, retained.BinSizeHz, retained.FftSize,
                    retained.Window, retained.Sums, dbv, retained.Meters, retained.PreviewFft, retained.PreviewScope));
            }

            return result?.ToArray() ?? [];
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

            var previous = _ringBuffers;
            var next = new SampleRingBuffer?[MaxChannels];
            for (int ch = 0; ch < MaxChannels; ch++)
            {
                next[ch] = new SampleRingBuffer(fftSize);

                // A fresh buffer starts with no discard booked, so a caller that changed FFT size
                // and stimulus together would lose the stimulus-latency discard the flush had just
                // set and refill this buffer with pre-change audio -- the very step the discard
                // exists to keep out of the first record. Carrying it across makes the resize safe
                // whichever order the two land in, which matters because the resize happens here on
                // the acquisition thread while the flush happens on the caller's.
                int pendingDiscard = previous[ch]?.DiscardRemaining ?? 0;
                if (pendingDiscard > 0)
                    next[ch]!.Reset(pendingDiscard);

                // SamplesWritten restarts at 0 on the replacement buffer -- see Flush.
                Volatile.Write(ref _lastAverageWritten[ch], 0);
            }

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

            if (options.SingleShot && Volatile.Read(ref _singleShotHeld[ch]))
                continue;

            var rb = ringBuffers[ch];
            if (rb is null || rb.Capacity != options.FftSize || !rb.IsFull)
                continue;

            // Checked here rather than inside ProcessChannel so a tick that has nothing new to add
            // skips the FFT entirely instead of computing one and throwing it away.
            if (!HasFreshSignalForAverage(ch, rb, options))
                continue;

            ProcessChannel(ch, rb, options);
        }
    }

    /// <summary>
    /// Whether enough new signal has arrived since this channel's last committed average to justify
    /// another one, per <see cref="SpectrumPipelineOptions.AverageHopFraction"/>.
    ///
    /// <para>Always true at the historical default of 0, and always true for the first analysis of
    /// an epoch — <c>IsFull</c> has already established that a complete, post-flush record exists,
    /// and there is no previous average for it to overlap with.</para>
    /// </summary>
    private bool HasFreshSignalForAverage(int channelIndex, SampleRingBuffer rb, SpectrumPipelineOptions options)
    {
        double hop = options.AverageHopFraction;
        if (hop <= 0.0)
            return true;

        long last = Volatile.Read(ref _lastAverageWritten[channelIndex]);
        if (last <= 0)
            return true;

        long needed = (long)Math.Ceiling(options.FftSize * Math.Min(hop, 1.0));
        return rb.SamplesWritten - last >= needed;
    }

    private void ProcessChannel(int channelIndex, SampleRingBuffer rb, SpectrumPipelineOptions options)
    {
        // The generation this record belongs to. Everything below describes the signal as it was
        // when this line ran; if a Flush lands while the FFT is in flight, that description is of
        // the *old* stimulus and must not be committed. See the check before the commit below.
        long generation = Interlocked.Read(ref _flushGeneration);

        int n = options.FftSize;
        int specLen = (n / 2) + 1;

        double[] timeBuf = ArrayPool<double>.Shared.Rent(n);
        double[] windowed = ArrayPool<double>.Shared.Rent(n);
        Complex[] spec = ArrayPool<Complex>.Shared.Rent(specLen);
        double[] ampBuf = ArrayPool<double>.Shared.Rent(specLen);
        double[] psdBuf = ArrayPool<double>.Shared.Rent(specLen);
        try
        {
            // Read alongside the samples it describes, not after the FFT: more samples land while
            // this tick runs, and crediting the average with those would let the next one through
            // early by however long the FFT took.
            long writtenAtSnapshot = rb.SamplesWritten;
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

            // Either averager reports the same count (both Updated together, just above, with the
            // same mode/count) — _psdAveragers is the arbitrary pick.
            int averagesTaken = _psdAveragers[channelIndex].Count;

            // Off has no "reached count" signal of its own (SpectrumAverager.Count stays 0 when
            // averaging is off), so a single shot without averaging just holds after this one tick.
            bool singleShotComplete = options.SingleShot && (options.Averaging == AveragingMode.Off || averagesTaken >= options.AverageCount);

            // A Flush while this tick was running means the samples it just analyzed predate a
            // stimulus change (a new generator frequency, a new range, a restart). Committing them
            // would re-arm `held` and overwrite the retained snapshot with pre-change audio — which
            // is exactly what made a THD-vs-frequency sweep report its first point's spectrum at
            // every subsequent frequency, and made the flush's own in-flight-discard pointless.
            // Drop the record instead; the next tick reads a window that is entirely post-change.
            //
            // Ahead of the `held` write below, not after it: a discarded record must leave no trace
            // at all, and holding the channel on one would stop the pipeline from ever producing the
            // fresh record that replaces it.
            if (Interlocked.Read(ref _flushGeneration) != generation)
                return;

            // Past the discard check, so this record is genuinely part of the average -- advance the
            // hop clock only now, or a dropped record would push the next accepted one a full hop
            // further out for no reason.
            Volatile.Write(ref _lastAverageWritten[channelIndex], writtenAtSnapshot);

            (float peakDbv, float totalRmsDbv) = ComputeTimeDomainLevels(timeBuf.AsSpan(0, n));
            var meters = ComputeMeters(psd, peakDbv, totalRmsDbv, options, averagesTaken);

            // Only computed for a sweep (see SpectrumPipelineOptions.CapturePreview) -- the ~2400x
            // more expensive full-resolution retained trace already exists for the live view, and a
            // second decimation of it on every one of ~20 ticks/sec would be pure waste there.
            SweepPreviewSeries? previewFft = null;
            SweepPreviewSeries? previewScope = null;
            if (options.CapturePreview)
            {
                previewFft = BuildFftPreview(ampPower, binSizeHz, options.DisplayLoFreqHz);
                previewScope = BuildScopePreview(timeBuf.AsSpan(0, n), _sampleRateHz, meters?.FundamentalFreqHz);
            }

            UpdateRetainedSnapshot(channelIndex, ampPower, binSizeHz, n, options.Window, window.Sums, meters, seq, previewFft, previewScope);

            // Only now, once the trace this signal refers to is actually retained. `held` is the
            // "your capture is ready" flag SingleShotComplete exposes, and ThdSweepRunner reads
            // LatestSnapshots() the moment it sees it — setting it before UpdateRetainedSnapshot
            // left a window where the capture was complete but the snapshot was still empty (or
            // still the previous point's), which showed up as a sweep point silently going missing.
            if (singleShotComplete)
                Volatile.Write(ref _singleShotHeld[channelIndex], true);

            BroadcastSpectrumFrame(channel, seq, ampPower, binSizeHz, options);
            if (meters is not null)
                BroadcastMetersFrame(channel, seq, meters, singleShotComplete);
            BroadcastScopeFrame(channel, seq, timeBuf.AsSpan(0, n), _sampleRateHz);
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

    /// <summary>
    /// Everything the meters panel shows, computed once per channel per tick and shared by both
    /// the live broadcast (<see cref="BroadcastMetersFrame"/>) and <see cref="LatestSnapshots"/> --
    /// so a saved measurement's metrics are exactly what was on screen, never re-derived
    /// separately. Returns <see langword="null"/> when no fundamental (and so no harmonics) could
    /// be found, matching the old inline logic's "nothing to broadcast this tick" behaviour.
    /// </summary>
    private static SpectrumMeters? ComputeMeters(PsdSpectrum psd, float peakDbv, float totalRmsDbv, SpectrumPipelineOptions options, int averagesTaken)
    {
        int minSearchBin = Math.Max(1, (int)Math.Round(options.FundamentalMinSearchHz / psd.BinSizeHz));
        int fundamentalBin = FindGlobalPeakBin(psd.ValuesV2PerHz, minSearchBin);
        if (fundamentalBin <= 0)
            return null;

        // The measurement bandwidth for every noise-integrated figure below, and the upper bound
        // on which harmonics count toward THD. See SpectrumPipelineOptions.MeasurementLoFreqHz —
        // passing this rather than the full spectrum is what keeps DC and out-of-band noise out
        // of THD+N/SNR.
        var band = psd.BandOf(options.MeasurementLoFreqHz, options.MeasurementHiFreqHz);

        // Harmonics may be counted past the measurement band -- see
        // SpectrumPipelineOptions.HarmonicMaxFreqHz. Everything else below stays on `band`.
        int harmonicMaxBin = options.HarmonicMaxFreqHz is { } harmonicMaxHz
            ? Math.Min(psd.ValuesV2PerHz.Length - 1, psd.BinOfFrequency(harmonicMaxHz))
            : band.HiBinInclusive;

        int harmonicCount = Math.Max(1, options.HarmonicCount);
        var harmonicsBuf = new Harmonic[harmonicCount];
        int written = HarmonicAnalyzer.Analyze(
            psd, fundamentalBin, harmonicCount, harmonicsBuf, maxBinInclusive: harmonicMaxBin);
        if (written == 0)
            return null;

        var harmonics = written == harmonicsBuf.Length ? harmonicsBuf : harmonicsBuf[..written];
        var fundamental = harmonics[0];

        float fundamentalFreqHz = (float)(fundamentalBin * psd.BinSizeHz);
        float thdPercent = (float)(SpectrumMetrics.Thd(harmonics) * 100.0);
        float thdNPercent = (float)(SpectrumMetrics.ThdPlusN(psd, fundamental, band, options.NotchOctaves) * 100.0);
        float snrDb = (float)SpectrumMetrics.SnrDb(psd, harmonics, band, options.NotchOctaves);
        float fundamentalDbv = (float)fundamental.AmplitudeDbv;
        float noiseFloorDbv = fundamentalDbv - snrDb;

        var otherPeaks = FindOtherPeaks(psd, harmonics, fundamentalDbv, options);

        return new SpectrumMeters(
            fundamentalFreqHz, (float)psd.BinSizeHz, thdPercent, thdNPercent, snrDb,
            fundamentalDbv, noiseFloorDbv, peakDbv, totalRmsDbv, harmonics, otherPeaks, averagesTaken);
    }

    /// <summary>
    /// How many of the most recent time-domain samples the oscilloscope view gets per tick,
    /// independent of FFT size — a 1,048,576-point FFT's whole window would be both far more data
    /// than a chart can usefully show and a multi-second-long "instant" that isn't a waveform
    /// snapshot anymore. Fixed rather than a fraction of FftSize so the on-screen time span only
    /// depends on sample rate (e.g. ~43 ms at 48 kHz, ~11 ms at 192 kHz), not on unrelated
    /// spectrum-analysis settings.
    /// </summary>
    private const int ScopeSampleCount = 2048;

    /// <summary>
    /// Broadcasts the tail of this tick's already-captured time-domain window as a scope frame —
    /// reuses <paramref name="timeDomainSamples"/> (the same raw, unwindowed snapshot
    /// <see cref="ComputeTimeDomainLevels"/> reads) rather than touching the ring buffer again, so
    /// this costs one more encode+broadcast per tick, not another snapshot.
    /// </summary>
    private void BroadcastScopeFrame(StreamChannel channel, uint seq, ReadOnlySpan<double> timeDomainSamples, double sampleRateHz)
    {
        int count = Math.Min(ScopeSampleCount, timeDomainSamples.Length);
        var tail = timeDomainSamples[^count..];

        float[] samples = ArrayPool<float>.Shared.Rent(count);
        try
        {
            for (int i = 0; i < count; i++)
                samples[i] = (float)tail[i];

            byte[] frame = StreamFrame.EncodeScope(channel, seq, (float)(1.0 / sampleRateHz), samples.AsSpan(0, count));
            hub.Broadcast(frame);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(samples);
        }
    }

    private void BroadcastMetersFrame(StreamChannel channel, uint seq, SpectrumMeters meters, bool held)
    {
        byte[] frame = StreamFrame.EncodeMeters(
            channel, seq, meters.FundamentalFreqHz, meters.BinSizeHz,
            meters.ThdPercent, meters.ThdNPercent, meters.SnrDb, meters.FundamentalDbv, meters.NoiseFloorDbv,
            meters.PeakDbv, meters.TotalRmsDbv, meters.Harmonics, meters.OtherPeaks, (uint)meters.AveragesTaken, held);
        hub.Broadcast(frame);
    }

    private void UpdateRetainedSnapshot(
        int channelIndex, ReadOnlySpan<double> ampPower, double binSizeHz, int fftSize,
        WindowType windowType, WindowSums sums, SpectrumMeters? meters, uint seq,
        SweepPreviewSeries? previewFft, SweepPreviewSeries? previewScope)
    {
        lock (_retainedLock)
        {
            var retained = _retained[channelIndex];
            if (retained.AmpPower is null || retained.AmpPower.Length != ampPower.Length)
                retained.AmpPower = new double[ampPower.Length];

            ampPower.CopyTo(retained.AmpPower);
            retained.CapturedUtc = DateTimeOffset.UtcNow;
            retained.SampleRateHz = _sampleRateHz;
            retained.BinSizeHz = binSizeHz;
            retained.FftSize = fftSize;
            retained.Window = windowType;
            retained.Sums = sums;
            retained.Meters = meters;
            retained.Seq = seq;
            retained.PreviewFft = previewFft;
            retained.PreviewScope = previewScope;
        }
    }

    /// <summary>
    /// At most this many (x, min, max) columns in a decimated sweep-point preview — small enough
    /// that a whole stereo sweep's worth of previews is negligible next to the full-resolution
    /// spectra a saved FFT result stores, generous enough that a waveform or a spectrum's harmonic
    /// structure still reads clearly on a small chart.
    /// </summary>
    public const int PreviewMaxColumns = 200;

    /// <summary>
    /// Decimates the current tick's (already-averaged) power spectrum into a small FFT preview,
    /// the same log-spaced min/max convention <see cref="BuildSpectrumFrame"/> uses for the live
    /// chart, just capped at far fewer columns. <paramref name="ampPower"/> is peak-volts squared,
    /// same as everywhere else in this class -- converted to dBV only for the handful of columns
    /// actually kept, not the full spectrum.
    /// </summary>
    private static SweepPreviewSeries BuildFftPreview(ReadOnlySpan<double> ampPower, double binSizeHz, double displayLoFreqHz)
    {
        int specLen = ampPower.Length;
        int loBin = Math.Clamp((int)Math.Round(displayLoFreqHz / binSizeHz), 1, specLen - 2);
        int hiBin = specLen - 1;
        int columnCount = Math.Clamp(hiBin - loBin + 1, 1, PreviewMaxColumns);

        var columns = new MinMax[columnCount];
        var centerBins = new double[columnCount];
        SpectrumDecimator.DecimateLogWithCenters(ampPower[..(hiBin + 1)], columns, centerBins, loBin);

        var freqHz = new float[columnCount];
        var dbvMin = new float[columnCount];
        var dbvMax = new float[columnCount];
        for (int i = 0; i < columnCount; i++)
        {
            freqHz[i] = (float)(centerBins[i] * binSizeHz);
            dbvMin[i] = (float)(10.0 * Math.Log10(Math.Max(columns[i].Min / 2.0, double.Epsilon)));
            dbvMax[i] = (float)(10.0 * Math.Log10(Math.Max(columns[i].Max / 2.0, double.Epsilon)));
        }

        return new SweepPreviewSeries(freqHz, dbvMin, dbvMax);
    }

    /// <summary>
    /// Decimates a window of this tick's raw (unwindowed) time-domain samples into a small
    /// oscilloscope preview, spanning <see cref="ScopeDisplayWindow.SecondsFor"/> of the located
    /// fundamental so a high-frequency sweep point isn't drawn as a barely-visible sliver. Falls
    /// back to the same span the live oscilloscope's own frame covers
    /// (<see cref="ScopeSampleCount"/>) when no fundamental was found -- the noise-floor pass has no
    /// tone to size a period-based window around.
    ///
    /// The window starts at the first rising zero-crossing rather than at a fixed tail offset --
    /// the same trigger search <c>OscilloscopeChart.vue</c>'s <c>findTriggerWindow</c> does for the
    /// live FFT screen's oscilloscope, ported here because this preview is decimated server-side
    /// (min/max, ≤ <see cref="PreviewMaxColumns"/> points): the raw per-sample resolution a crossing
    /// search needs does not survive that decimation, so unlike the live view this has to trigger
    /// before it shrinks the data, not after. Each channel triggers off its own crossing rather than
    /// sharing one the way the live view's two channels do (see that component's remarks on why it
    /// picks one trigger source for both) -- SpectrumPipeline processes channels independently and
    /// has no per-tick view of the other one's samples to share a trigger point with, and for a
    /// diagnostic preview (not a phase measurement) each trace reading as a clean waveform matters
    /// more than the two staying phase-locked to each other.
    ///
    /// Values are decimated linearly (not log, unlike the FFT preview): a waveform's x-axis is
    /// time, which has no meaningful log spacing.
    /// </summary>
    private SweepPreviewSeries BuildScopePreview(ReadOnlySpan<double> timeDomainSamples, double sampleRateHz, float? fundamentalFreqHz)
    {
        int n = timeDomainSamples.Length;
        double displaySeconds = fundamentalFreqHz is { } f && f > 0.0
            ? ScopeDisplayWindow.SecondsFor(f)
            : ScopeSampleCount / sampleRateHz;

        int sampleCount = Math.Clamp((int)Math.Round(displaySeconds * sampleRateHz), 1, n);
        ReadOnlySpan<double> window;

        if (fundamentalFreqHz is { } freqHz && freqHz > 0.0)
        {
            // Widened by one period so a crossing found near the end of the search buffer still
            // leaves room for the full display window after it -- matching a display window
            // narrower than what was searched (see findTriggerWindow's own "available" clamp) is
            // the exception, not the rule.
            double periodSamples = sampleRateHz / freqHz;
            int searchBufferCount = Math.Clamp(sampleCount + (int)Math.Ceiling(periodSamples), 1, n);
            var searchBuffer = timeDomainSamples[^searchBufferCount..];

            // Bounded to one period, same as the live view: a periodic signal cannot need more than
            // that to find its own rising edge, and an unbounded search risks eating the very buffer
            // the window is measured against on a tone riding a large low-frequency component.
            int searchLimit = Math.Min(searchBuffer.Length, (int)Math.Ceiling(periodSamples) + 1);
            int crossingIndex = 0;
            for (int i = 1; i < searchLimit; i++)
            {
                if (searchBuffer[i - 1] < 0.0 && searchBuffer[i] >= 0.0)
                {
                    crossingIndex = i;
                    break;
                }
            }

            // No crossing found means there is no clean one to find (the live view's own fallback);
            // starting at the raw beginning of the search buffer is no worse.
            int available = searchBuffer.Length - crossingIndex;
            window = searchBuffer.Slice(crossingIndex, Math.Min(sampleCount, available));
        }
        else
        {
            window = timeDomainSamples[^sampleCount..];
        }

        int columnCount = Math.Clamp(window.Length, 1, PreviewMaxColumns);
        var columns = new MinMax[columnCount];
        SpectrumDecimator.DecimateLinear(window, columns);

        // The decimator's own column boundaries are evenly spaced up to integer rounding, so a
        // column's center time is recovered from its index rather than threaded out of Decimate as
        // a second array (as DecimateLogWithCenters does for the log case, where the boundaries are
        // NOT evenly spaced and reconstructing them would be wrong) -- see that method's remarks.
        double actualSeconds = window.Length / sampleRateHz;
        var timeS = new float[columnCount];
        var vMin = new float[columnCount];
        var vMax = new float[columnCount];
        for (int i = 0; i < columnCount; i++)
        {
            timeS[i] = (float)((i + 0.5) / columnCount * actualSeconds);
            vMin[i] = (float)columns[i].Min;
            vMax[i] = (float)columns[i].Max;
        }

        return new SweepPreviewSeries(timeS, vMin, vMax);
    }

    /// <summary>Mutable per-channel retention slot backing <see cref="_retained"/> -- see its own remarks.</summary>
    private sealed class RetainedSpectrum
    {
        public double[]? AmpPower;
        public DateTimeOffset CapturedUtc;
        public double SampleRateHz;
        public double BinSizeHz;
        public int FftSize;
        public WindowType Window;
        public WindowSums Sums;
        public SpectrumMeters? Meters;
        public uint Seq;
        public SweepPreviewSeries? PreviewFft;
        public SweepPreviewSeries? PreviewScope;
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
