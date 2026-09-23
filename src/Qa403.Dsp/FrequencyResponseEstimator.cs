using System.Numerics;
using Qa403.Dsp.Fft;

namespace Qa403.Dsp;

/// <summary>
/// One finished transfer-function estimate. All arrays are one-sided, <c>fftSize / 2 + 1</c> long,
/// bin <c>k</c> at <c>k · binHz</c>.
/// </summary>
/// <param name="Magnitude">Linear magnitude of H, i.e. output volts per input volt. Not yet in dB, so it can still be smoothed (see <see cref="FractionalOctaveSmoother"/>).</param>
/// <param name="PhaseDeg">Phase of H in degrees, unwrapped across bins, with the bulk propagation delay already removed.</param>
/// <param name="Coherence">|Sxy|²/(Sxx·Syy) in [0,1]. <b>Identically 1 for a single record</b> — see <see cref="FrequencyResponseEstimator.Records"/>.</param>
/// <param name="Confidence">Stimulus magnitude per bin, normalised to its own maximum. Near zero outside the swept band, where H is meaningless however good the coherence looks.</param>
/// <param name="LatencySamples">
/// Mean removed rotation, in samples, across the accumulated records. This is a <i>delay</i> only
/// when the reference is a second measured channel: against a synthesised periodic stimulus the
/// record starts at an arbitrary point in the sweep, so the number is that arbitrary offset and
/// carries no information about the DUT. See <see cref="FrequencyResponseEstimator.Add"/>.
/// </param>
public sealed record FrequencyResponseResult(
    double[] Magnitude,
    double[] PhaseDeg,
    double[] Coherence,
    double[] Confidence,
    double LatencySamples);

/// <summary>
/// The impulse responses behind a finished estimate, as
/// <see cref="FrequencyResponseEstimator.CompleteImpulse"/> returns them.
///
/// <para>Both are needed and neither substitutes for the other. The <paramref name="Gated"/> one is
/// the linear response — what the display, the gated magnitude and phase, and any decay analysis
/// read. The <paramref name="Ungated"/> one is the <i>only</i> place the DUT's harmonic distortion
/// packets still exist; the gate's whole purpose is to delete them, so
/// <see cref="FrequencyResponseEstimator.Complete"/> throws them away by construction.</para>
/// </summary>
/// <param name="Ungated">
/// IFFT of the accumulated H: circular, <c>fftSize</c> long, in the record's own rotation.
/// </param>
/// <param name="Gated">
/// The same array with the estimator's <see cref="ImpulseGate"/> applied about
/// <paramref name="PeakIndex"/>. A copy of <paramref name="Ungated"/> when the estimator was
/// constructed ungated.
/// </param>
/// <param name="PeakIndex">
/// Index of the largest-magnitude sample of <paramref name="Ungated"/> — the linear impulse, which
/// outweighs every distortion packet by orders of magnitude. Not rotated to zero: the packet
/// extractor indexes backwards from it, a display gate indexes either side of it, and only a wire
/// encoder wants it at the origin. Rotating here would force two of those three to undo it.
/// </param>
/// <param name="AppliedRotationSamples">
/// The mean per-record rotation <see cref="FrequencyResponseEstimator.Add"/> already removed.
/// <b>Fractional</b> — <c>EstimateLatencySamples</c> refines the peak with a parabola — so these
/// arrays are a bandlimited (sinc-interpolated) shift of the DUT's true sampled impulse response,
/// not a sample-aligned copy of it. A test comparing sample-for-sample against an analytic
/// <c>h[n]</c> must apply the same shift, or it fails around −20 dB for a reason that is not a bug.
/// </param>
public sealed record ImpulseResponseResult(
    double[] Ungated,
    double[] Gated,
    int PeakIndex,
    double AppliedRotationSamples);

/// <summary>
/// A circular window applied to the estimated impulse response before magnitude and phase are read
/// off it, measured in samples either side of the linear impulse.
///
/// <para><b>Why an estimate needs one.</b> Deconvolving an exponential sweep does not only recover
/// the linear impulse response. The DUT's k-th harmonic arrives as its own packet, <i>ahead</i> of
/// the linear one by <c>N·ln(k)/ln(f₂/f₁)</c> samples — and "ahead" over a circular record means
/// folded to the far end of the buffer, i.e. still inside it. Nothing separates those packets in
/// the frequency domain; they add straight into H as a comb whose spacing is the reciprocal of
/// their offset, so a DUT that distorts over one part of the band grows ripple over exactly that
/// part. Measured on a push-pull tube amplifier, 192 kHz, 524288-sample sweep: 0.24 dB rms of
/// ripple over 20–40 Hz spaced <b>3.494 Hz</b>, against <b>3.586 Hz</b> predicted for the third
/// harmonic, falling to 0.002 dB rms by 160 Hz where the output transformer stops distorting.
/// Deleting the stretch of record those packets live in is the only thing that removes them —
/// smoothing cannot, averaging cannot (they are deterministic, coherence stayed at 0.9985), and
/// taking the reference from a second measured channel cannot, because that channel sees the
/// stimulus rather than the DUT's output.</para>
///
/// <para><b>The bounds are not free parameters.</b> <see cref="PreSamples"/> has to stop short of
/// the nearest (second-harmonic) packet or the gate keeps what it exists to discard;
/// <see cref="PostSamples"/> has to stop short of that same packet coming the other way round the
/// circle. Between those, length is a resolution choice — a gate of L samples cannot resolve
/// anything narrower than <c>fs/L</c> — and it is the long record that makes this affordable here:
/// gating a 2.73 s sweep to 1.45 s costs 0.69 Hz against 0.37 Hz bins, while the previous app's
/// 355 ms sweep would have had its packets only 31 ms apart.</para>
/// </summary>
/// <param name="PreSamples">Samples kept before the linear impulse. Must be less than the second-harmonic offset.</param>
/// <param name="PostSamples">Samples kept after it.</param>
/// <param name="TaperSamples">Raised-cosine taper at each edge, so the cut does not ring across the spectrum.</param>
public readonly record struct ImpulseGate(int PreSamples, int PostSamples, int TaperSamples)
{
    /// <summary>
    /// The gate for a sweep from <paramref name="startFreqHz"/> to <paramref name="endFreqHz"/>
    /// occupying one whole <paramref name="fftSize"/>-sample period: half way back to the
    /// second-harmonic packet, and forward to half the period.
    ///
    /// <para>Stopping at half the period is what bounds the harmonic order rejected: the packet at
    /// <c>N/2</c> is the one for <c>k = √(f₂/f₁)</c>, which for a 2 Hz–94 kHz sweep is the
    /// <b>217th</b> harmonic. Everything from the 2nd to the 217th falls in the discarded stretch.</para>
    /// </summary>
    public static ImpulseGate ForSweep(int fftSize, double startFreqHz, double endFreqHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fftSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(startFreqHz);
        if (endFreqHz <= startFreqHz)
            throw new ArgumentOutOfRangeException(nameof(endFreqHz), "End frequency must exceed the start frequency.");

        double secondHarmonicSamples = fftSize * Math.Log(2.0) / Math.Log(endFreqHz / startFreqHz);
        int pre = Math.Max(1, (int)(secondHarmonicSamples / 2.0));

        return new ImpulseGate(pre, fftSize / 2, Math.Max(1, pre / 2));
    }
}

/// <summary>
/// Estimates a transfer function from a known (or measured) reference spectrum and a response
/// spectrum, accumulating over repeated records.
///
/// <para><b>The estimator is H1</b>, <c>H = Sxy / (Sxx + λ)</c> with <c>λ = 1e-4·max(Sxx)</c>
/// (following <c>qa40x-rs/src-tauri/src/audio/frequency_response.rs:83-101</c>). The regulariser is
/// what keeps the band edges finite: an exponential sweep puts almost no energy above its end
/// frequency, and a bare <c>Y/X</c> there divides noise by nearly nothing and draws a wall of
/// garbage. H1 is biased when the <i>reference</i> itself is noisy, which is the case when the
/// right input channel is the reference rather than the synthesised stimulus; that is the standard
/// H1/H2 trade and is accepted here, since the alternative biases the far more common case.</para>
///
/// <para><b>Latency is removed per record, before accumulating, and that ordering is load-bearing.</b>
/// Each record starts at an arbitrary point in the periodic stimulus, so its H carries a different
/// linear phase ramp. Averaging complex spectra across records without removing it makes them
/// cancel — more averaging would produce a <i>worse</i> result, which is a hard bug to read from
/// the output. Removing it also removes genuine DUT and cable delay, which is what makes the phase
/// trace legible at all.</para>
///
/// <para>Pure and unit-tested; nothing here touches a device or the pipeline.</para>
/// </summary>
public sealed class FrequencyResponseEstimator
{
    /// <summary>
    /// Regularisation as a fraction of the strongest bin's reference power: small enough not to
    /// touch the swept band, large enough to bound the decades either side of it.
    ///
    /// <para>The bias it costs is exact and one-sided — every bin is scaled by
    /// <c>1/(1 + λ/Sxx)</c>, i.e. <c>1/(1 + fraction/confidence²)</c> — so it is largest where the
    /// sweep has least energy. The reference implementation
    /// (<c>qa40x-rs/.../frequency_response.rs:85</c>) uses 1e-4, which measures here as up to
    /// <b>0.10 dB</b> of droop inside the swept band. That is small but not negligible against the
    /// numbers this measurement exists to report, and it is pure give-away: 1e-6 still bounds an
    /// out-of-band bin by a factor of 100 against noise, while the in-band cost falls to about
    /// <b>0.001 dB</b>. Accuracy where the user reads the trace beats damping where they do not.
    /// Pinned by <c>FrequencyResponseEstimatorTests</c>, which predicts the bias rather than
    /// tolerating it.</para>
    /// </summary>
    public const double RegularisationFraction = 1e-6;

    /// <summary>
    /// The fraction for a stimulus that excites <b>every</b> bin — continuous noise, where nothing in
    /// the analysed band is starved and the regulariser has nothing to bound.
    ///
    /// <para>A sweep needs the default because it genuinely has dead bins: below its start frequency
    /// and above its end there is no stimulus at all, and a bare <c>Y/X</c> there divides noise by
    /// nearly nothing. Noise has no such decades — but it does have a wide dynamic range within the
    /// band (pink falls 3 dB an octave, so 30 dB across ten of them) <i>and</i> a per-bin magnitude
    /// that is a random draw, so the weakest bins of a column sit far below <c>max(Sxx)</c> and take
    /// the whole of the bias. Measured end to end against the resistive fixture at 24 kHz span: the
    /// default reads <b>0.11 dB</b> low at 10 kHz, this reads 0.001 dB. It is still a thousandfold
    /// guard against a bin that is exactly zero.</para>
    /// </summary>
    public const double BroadbandRegularisationFraction = 1e-9;

    private readonly IFftBackend _fft;
    private readonly int _fftSize;
    private readonly int _binCount;

    private readonly Complex[] _sxy;
    private readonly double[] _sxx;
    private readonly double[] _syy;

    private readonly Complex[] _h;
    private readonly double[] _impulse;

    /// <summary>The gate as a per-sample multiplier centred on index 0, or null when ungated.</summary>
    private readonly double[]? _gateWindow;

    /// <summary>See the constructor's <c>removeLatency</c> parameter.</summary>
    private readonly bool _removeLatency;

    /// <summary>See the constructor's <c>regularisationFraction</c> parameter.</summary>
    private readonly double _regularisationFraction;

    private double _latencySum;
    private int _records;

    /// <summary>
    /// The sliding window, or null when the average is unbounded. Each slot holds one record's own
    /// contribution to the three sums, so that evicting it is exact. See the constructor's
    /// <c>windowRecords</c> parameter.
    /// </summary>
    private readonly WindowSlot[]? _window;
    private int _windowNext;

    /// <param name="gate">
    /// Applied to the impulse response in <see cref="Complete"/>. Null — the default — leaves the
    /// estimate exactly as H1 produced it, which is what the golden tests pin; a gate is only
    /// meaningful when the stimulus was a sweep, since it is sweep deconvolution that puts the
    /// distortion packets there in the first place.
    /// </param>
    /// <param name="removeLatency">
    /// Whether to find and remove each record's bulk rotation before accumulating it. True -- the
    /// default -- is what every sweep measurement needs, for the reason set out on <see cref="Add"/>:
    /// against a synthesised periodic stimulus each record starts at an arbitrary point in the sweep,
    /// and averaging those rotations without removing them makes the records cancel.
    ///
    /// <para><b>Pass false when both spectra are measured channels of the <i>same</i> capture</b>, as
    /// the impedance measurement does. There the rotation is common to the two and has already
    /// cancelled in the ratio, so there is nothing left to remove and the peak search is fitting
    /// noise: <see cref="EstimateLatencySamples"/> refines the impulse's position with a parabola
    /// through its neighbours, so it returns a <i>fraction</i> of a sample even for a fixture with no
    /// delay at all. Half a sample is <b>75 degrees</b> of phase at 20 kHz on a 48 kHz record, and it
    /// is reported as DUT phase with nothing there to complain about it.</para>
    ///
    /// <para>Gating is unaffected either way: <see cref="ApplyGate"/> locates itself on the impulse
    /// rather than assuming removal has parked it at the origin.</para>
    /// </param>
    /// <param name="regularisationFraction">
    /// The <c>lambda</c> in <c>H = Sxy / (Sxx + lambda)</c>, as a fraction of the strongest bin's
    /// reference power. Defaults to <see cref="RegularisationFraction"/>, which is what a swept
    /// measurement needs; a stimulus that reaches every bin should pass
    /// <see cref="BroadbandRegularisationFraction"/> instead, and that member says why.
    /// </param>
    /// <param name="windowRecords">
    /// Zero -- the default -- accumulates every record ever added. A positive count makes the
    /// estimate a <b>sliding window</b> over the last that many records: once the window is full,
    /// each new record evicts the oldest, so the average neither grows without bound nor has to be
    /// restarted. That is what a continuous noise measurement wants -- its records are independent
    /// draws, so the estimate after record N+1 is as good as the one after record N, where
    /// restarting the average every N records throws a converged curve away and shows the
    /// single-record one in its place, the worst curve of the run, once every pass.
    ///
    /// <para>Eviction is exact rather than incremental: every record's own contribution is kept and
    /// the sums are rebuilt from the window on each add, so a window that has slid ten thousand
    /// times holds exactly the sum of its last N records and not the residue of subtracting
    /// nearly-equal floating-point numbers that many times. It costs N passes over the bins per
    /// add, trivial against the FFT that produced the record, and N slots of memory -- three arrays
    /// of bins each, about 8 MB per slot at the longest record -- which is why it is opt-in.</para>
    /// </param>
    public FrequencyResponseEstimator(
        int fftSize,
        IFftBackend fft,
        ImpulseGate? gate = null,
        bool removeLatency = true,
        double regularisationFraction = RegularisationFraction,
        int windowRecords = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(regularisationFraction);
        ArgumentOutOfRangeException.ThrowIfNegative(windowRecords);

        if (fftSize < 4 || (fftSize & (fftSize - 1)) != 0)
            throw new ArgumentException("FFT size must be a power of two of at least 4.", nameof(fftSize));

        if (gate is { } g)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(g.PreSamples, "gate.PreSamples");
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(g.PostSamples, "gate.PostSamples");
            ArgumentOutOfRangeException.ThrowIfNegative(g.TaperSamples, "gate.TaperSamples");
            if (g.PreSamples + g.PostSamples + 1 > fftSize)
                throw new ArgumentOutOfRangeException(nameof(gate), "A gate cannot be longer than the record it gates.");
            if (2 * g.TaperSamples > g.PreSamples + g.PostSamples + 1)
                throw new ArgumentOutOfRangeException(nameof(gate), "The tapers cannot overlap: the gate would never reach unity.");
        }

        _fft = fft ?? throw new ArgumentNullException(nameof(fft));
        _fftSize = fftSize;
        _binCount = (fftSize / 2) + 1;

        _sxy = new Complex[_binCount];
        _sxx = new double[_binCount];
        _syy = new double[_binCount];
        _h = new Complex[_binCount];
        _impulse = new double[fftSize];
        _gateWindow = gate is { } window ? BuildGateWindow(fftSize, window) : null;
        _removeLatency = removeLatency;
        _regularisationFraction = regularisationFraction;

        if (windowRecords > 0)
        {
            _window = new WindowSlot[windowRecords];
            for (int i = 0; i < windowRecords; i++)
                _window[i] = new WindowSlot(new Complex[_binCount], new double[_binCount], new double[_binCount]);
        }
    }

    /// <summary>One record's own contribution to the sums, kept so the window can evict it exactly.</summary>
    private sealed class WindowSlot(Complex[] sxy, double[] sxx, double[] syy)
    {
        public readonly Complex[] Sxy = sxy;
        public readonly double[] Sxx = sxx;
        public readonly double[] Syy = syy;
        public double LatencySamples;
        public bool Filled;
    }

    /// <summary>Records currently in the average: everything added, or the last <c>windowRecords</c> of them. <see cref="FrequencyResponseResult.Coherence"/> is only informative from 2 up.</summary>
    public int Records => _records;

    /// <summary>The window length, or 0 for an unbounded average.</summary>
    public int WindowRecords => _window?.Length ?? 0;

    public int BinCount => _binCount;

    /// <summary>
    /// Folds one record in and returns the rotation, in samples, that was removed from it. Both
    /// spectra must be one-sided and <see cref="BinCount"/> long, and — critically — must come from
    /// the <i>same</i> instant of the same capture, or the phase relationship they encode is fiction.
    ///
    /// <para><b>The returned number is a delay only when the reference is measured.</b> Against a
    /// synthesised periodic stimulus there is no trigger and the record starts wherever it starts,
    /// so what comes back is that arbitrary offset — uniform over the whole period, differing run to
    /// run, and saying nothing whatever about the DUT. Reported as a "sample delay" it invites
    /// exactly the misreading it once got: an offset that happened to land near a harmonic packet's
    /// position was taken as evidence the peak search had locked onto that packet. When the
    /// reference is a second channel of the <i>same</i> capture the rotation is common to both and
    /// cancels, and only then is this the DUT's own propagation delay.</para>
    ///
    /// <para>Returns 0 and removes nothing when the estimator was built with
    /// <c>removeLatency: false</c> -- see that parameter for when a same-capture ratio needs it
    /// off.</para>
    /// </summary>
    public double Add(ReadOnlySpan<Complex> reference, ReadOnlySpan<Complex> response)
    {
        if (reference.Length != _binCount)
            throw new ArgumentException($"Reference must have {_binCount} bins, got {reference.Length}.", nameof(reference));
        if (response.Length != _binCount)
            throw new ArgumentException($"Response must have {_binCount} bins, got {response.Length}.", nameof(response));

        double lambda = _regularisationFraction * MaxPower(reference);

        for (int k = 0; k < _binCount; k++)
        {
            double xx = (reference[k] * Complex.Conjugate(reference[k])).Real;
            _h[k] = response[k] * Complex.Conjugate(reference[k]) / (xx + lambda);
        }

        double latencySamples = _removeLatency ? EstimateLatencySamples() : 0.0;

        if (_window is null)
        {
            for (int k = 0; k < _binCount; k++)
            {
                Complex aligned = response[k] * Rotation(k, latencySamples);
                _sxy[k] += aligned * Complex.Conjugate(reference[k]);
                _sxx[k] += (reference[k] * Complex.Conjugate(reference[k])).Real;
                _syy[k] += (aligned * Complex.Conjugate(aligned)).Real;
            }

            _latencySum += latencySamples;
            _records++;
            return latencySamples;
        }

        // Windowed: this record takes the oldest slot (or the next empty one), then the sums are
        // rebuilt from whatever the window holds. See the constructor for why not subtract-and-add.
        var slot = _window[_windowNext];
        _windowNext = (_windowNext + 1) % _window.Length;

        for (int k = 0; k < _binCount; k++)
        {
            Complex aligned = response[k] * Rotation(k, latencySamples);
            slot.Sxy[k] = aligned * Complex.Conjugate(reference[k]);
            slot.Sxx[k] = (reference[k] * Complex.Conjugate(reference[k])).Real;
            slot.Syy[k] = (aligned * Complex.Conjugate(aligned)).Real;
        }

        slot.LatencySamples = latencySamples;
        slot.Filled = true;

        Array.Clear(_sxy);
        Array.Clear(_sxx);
        Array.Clear(_syy);
        _latencySum = 0.0;
        _records = 0;

        foreach (var filled in _window)
        {
            if (!filled.Filled)
                continue;

            for (int k = 0; k < _binCount; k++)
            {
                _sxy[k] += filled.Sxy[k];
                _sxx[k] += filled.Sxx[k];
                _syy[k] += filled.Syy[k];
            }

            _latencySum += filled.LatencySamples;
            _records++;
        }

        return latencySamples;
    }

    /// <summary>Finishes the estimate. Throws if nothing has been added — an empty average has no defined value.</summary>
    public FrequencyResponseResult Complete()
    {
        if (_records == 0)
            throw new InvalidOperationException("No records have been accumulated.");

        double maxSxx = 0.0;
        for (int k = 0; k < _binCount; k++)
            maxSxx = Math.Max(maxSxx, _sxx[k]);

        double lambda = _regularisationFraction * maxSxx;
        double referenceScale = maxSxx > 0.0 ? Math.Sqrt(maxSxx) : 1.0;

        double[] magnitude = new double[_binCount];
        double[] phaseDeg = new double[_binCount];
        double[] coherence = new double[_binCount];
        double[] confidence = new double[_binCount];

        for (int k = 0; k < _binCount; k++)
            _h[k] = _sxy[k] / (_sxx[k] + lambda);

        ApplyGate();

        // Unwrap as we go: keep each step within half a turn of the last, so a response that
        // accumulates several turns of phase reads as a continuous curve rather than a sawtooth.
        // Shared with the stepped-sine response, which does the same across sweep points -- see
        // PhaseUnwrapper. A bin below the phase floor is held at the last unwrapped value instead
        // of being unwrapped: its phase is roundoff, and unwrapping through a stretch of roundoff
        // random-walks the turn count -- see PhaseFloorRelative for the measured case.
        var unwrapper = new PhaseUnwrapper();
        double phaseFloor = 0.0;
        for (int k = 0; k < _binCount; k++)
            phaseFloor = Math.Max(phaseFloor, _h[k].Magnitude);
        phaseFloor *= PhaseFloorRelative;

        for (int k = 0; k < _binCount; k++)
        {
            Complex h = _h[k];
            magnitude[k] = h.Magnitude;

            phaseDeg[k] = h.Magnitude > phaseFloor ? unwrapper.NextFromRadians(h.Phase) : unwrapper.Hold();

            double denominator = _sxx[k] * _syy[k];
            coherence[k] = denominator > 0.0
                ? Math.Clamp((_sxy[k] * Complex.Conjugate(_sxy[k])).Real / denominator, 0.0, 1.0)
                : 0.0;

            confidence[k] = Math.Sqrt(_sxx[k]) / referenceScale;
        }

        return new FrequencyResponseResult(magnitude, phaseDeg, coherence, confidence, _latencySum / _records);
    }

    /// <summary>
    /// The impulse response behind the current estimate, gated and ungated. Throws if nothing has
    /// been accumulated, on the same contract as <see cref="Complete"/>.
    ///
    /// <para><b>This recomputes H into a scratch array rather than reading <c>_h</c>, and that is
    /// load-bearing.</b> <see cref="Complete"/> gates <c>_h</c> <i>in place</i>, so it is not
    /// idempotent: reading <c>_h</c> here would make the impulse response depend on whether
    /// <see cref="Complete"/> had already been called, and on how many times. Pinned by
    /// <c>CompleteImpulse_AfterComplete_ReturnsTheSameIr</c>.</para>
    ///
    /// <para>Costs one extra inverse transform, which is why it is a separate call rather than part
    /// of <see cref="Complete"/>: only the impulse-response measurement wants it.</para>
    /// </summary>
    public ImpulseResponseResult CompleteImpulse()
    {
        if (_records == 0)
            throw new InvalidOperationException("No records have been accumulated.");

        double maxSxx = 0.0;
        for (int k = 0; k < _binCount; k++)
            maxSxx = Math.Max(maxSxx, _sxx[k]);

        double lambda = _regularisationFraction * maxSxx;

        var h = new Complex[_binCount];
        for (int k = 0; k < _binCount; k++)
            h[k] = _sxy[k] / (_sxx[k] + lambda);

        double[] ungated = new double[_fftSize];
        _fft.Inverse(h, ungated);

        int peak = PeakIndex(ungated);

        double[] gated = new double[_fftSize];
        if (_gateWindow is null)
        {
            ungated.CopyTo(gated, 0);
        }
        else
        {
            for (int i = 0; i < _fftSize; i++)
                gated[i] = ungated[i] * _gateWindow[((i - peak) % _fftSize + _fftSize) % _fftSize];
        }

        return new ImpulseResponseResult(ungated, gated, peak, _latencySum / _records);
    }

    public void Reset()
    {
        Array.Clear(_sxy);
        Array.Clear(_sxx);
        Array.Clear(_syy);
        _latencySum = 0.0;
        _records = 0;

        if (_window is not null)
        {
            foreach (var slot in _window)
                slot.Filled = false;
            _windowNext = 0;
        }
    }

    /// <summary>
    /// Windows <see cref="_h"/>'s impulse response in place, discarding the stretch of the record
    /// that a sweep deconvolution fills with harmonic distortion packets. A no-op when ungated.
    ///
    /// <para>The gate locates itself on the impulse rather than assuming index 0. Per-record
    /// latency removal does park it there, so the two normally agree — but the gate is the one
    /// thing here that can throw away the <i>linear</i> response if it is centred wrongly, and
    /// paying one peak search to make it self-locating is cheaper than trusting an invariant that
    /// is enforced somewhere else.</para>
    /// </summary>
    private void ApplyGate()
    {
        if (_gateWindow is null)
            return;

        _fft.Inverse(_h, _impulse);

        int centre = PeakIndex(_impulse);
        for (int i = 0; i < _fftSize; i++)
            _impulse[i] *= _gateWindow[((i - centre) % _fftSize + _fftSize) % _fftSize];

        _fft.Forward(_impulse, _h);
    }

    /// <summary>
    /// The gate as a multiplier per sample, indexed by circular offset from the impulse: unity
    /// across the kept span, a raised-cosine taper at each edge, zero everywhere else.
    /// </summary>
    private static double[] BuildGateWindow(int fftSize, ImpulseGate gate)
    {
        var window = new double[fftSize];

        for (int offset = -gate.PreSamples; offset <= gate.PostSamples; offset++)
        {
            int fromEdge = Math.Min(offset + gate.PreSamples, gate.PostSamples - offset);
            window[(offset % fftSize + fftSize) % fftSize] = gate.TaperSamples <= 0 || fromEdge >= gate.TaperSamples
                ? 1.0
                : 0.5 * (1.0 - Math.Cos(Math.PI * fromEdge / gate.TaperSamples));
        }

        return window;
    }

    /// <summary>Index of the largest sample in magnitude — the linear impulse, which outweighs every distortion packet it is separated from.</summary>
    private static int PeakIndex(double[] impulse)
    {
        int peak = 0;
        double best = -1.0;
        for (int i = 0; i < impulse.Length; i++)
        {
            double magnitude = Math.Abs(impulse[i]);
            if (magnitude > best)
            {
                best = magnitude;
                peak = i;
            }
        }

        return peak;
    }

    /// <summary>
    /// The bulk delay of the current <see cref="_h"/>, in samples: the impulse response's largest
    /// peak, refined to a fraction of a sample by fitting a parabola through it and its neighbours.
    ///
    /// <para>Indices past the halfway point are read as negative delays. Over a circular record a
    /// delay of <c>N−3</c> and one of <c>−3</c> are the same rotation, and the negative reading is
    /// the one that stays continuous when the true delay hovers near zero — as it does when the
    /// reference is the right input channel rather than the synthesised stimulus.</para>
    /// </summary>
    private double EstimateLatencySamples()
    {
        _fft.Inverse(_h, _impulse);

        int peak = PeakIndex(_impulse);

        double left = Math.Abs(_impulse[(peak - 1 + _fftSize) % _fftSize]);
        double centre = Math.Abs(_impulse[peak]);
        double right = Math.Abs(_impulse[(peak + 1) % _fftSize]);

        // Vertex of the parabola through the three points, in bins from the centre. The denominator
        // vanishes only when all three are equal, i.e. there is no peak to refine.
        double denominator = left - (2.0 * centre) + right;
        double offset = Math.Abs(denominator) > double.Epsilon
            ? 0.5 * (left - right) / denominator
            : 0.0;
        offset = Math.Clamp(offset, -0.5, 0.5);

        double samples = peak + offset;
        if (samples > _fftSize / 2.0)
            samples -= _fftSize;

        return samples;
    }

    /// <summary>The phase advance that undoes a delay of <paramref name="latencySamples"/> at bin <paramref name="k"/>.</summary>
    private Complex Rotation(int k, double latencySamples) =>
        Complex.FromPolarCoordinates(1.0, 2.0 * Math.PI * k * latencySamples / _fftSize);

    private static double MaxPower(ReadOnlySpan<Complex> spectrum)
    {
        double max = 0.0;
        for (int k = 0; k < spectrum.Length; k++)
            max = Math.Max(max, (spectrum[k] * Complex.Conjugate(spectrum[k])).Real);
        return max;
    }

    /// <summary>
    /// Below this fraction of the largest |H| a bin's phase is not unwrapped but held (see
    /// <see cref="Complete"/>). 1e-6 is 120 dB down: the QA403's own dynamic range, and beyond
    /// what any phase measurement on it can mean. Found on the simulated Bessel over a 96 kHz span
    /// with the noise off: the response falls to the float32 sample format's rounding at about
    /// −134 dB by 52 kHz, every bin above that is rounding with a uniformly random phase -- and,
    /// being deterministic, rounding repeats identically across averages, so coherence stays at
    /// 1.0 and cannot catch it -- and unwrapping through five thousand such bins accumulated
    /// −1 311 194° by 95 kHz. That put the Nichols phase axis at −1e6°, drew its −180° markers as a
    /// solid band and gave the margin search 3 377 phantom crossings. A real device's floor is noise
    /// rather than rounding and its coherence collapses there, which is the ordinary defence; this
    /// is for a floor coherence cannot see. Holding rather than zeroing keeps the turn count, so a
    /// response that comes back up (a band-stop) unwraps on from the last measured bin.
    /// </summary>
    public const double PhaseFloorRelative = 1e-6;

    /// <summary>dB from a linear magnitude, floored so an exactly-zero bin yields a finite number.</summary>
    public static double ToDb(double magnitude) => 20.0 * Math.Log10(Math.Max(magnitude, 1e-30));
}
