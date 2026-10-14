using Tonebench.App.Streaming;
using Tonebench.Dsp;

namespace Tonebench.App.Services;

/// <summary>
/// The capture mechanics the stepped sweep runners share — waiting for a single shot, the timeout
/// that bounds it, and the readings taken off a snapshot. Pure arithmetic and one polling loop;
/// the runners' control flow (what to play, how to range, what a point is) stays in each runner
/// on purpose, the way <see cref="ResponseCaptureSupport"/> keeps the response runners' out.
/// </summary>
internal static class SweepCaptureSupport
{
    /// <summary>How often <see cref="WaitForSingleShotAsync"/> re-checks. Short against the 20 Hz pipeline tick, so it costs a poll or two per average.</summary>
    public static readonly TimeSpan CapturePollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// The stimulus latency the flush discards is ~65 ms at every sample rate (see
    /// <c>Qa40xUsbDevice.StimulusLatencyFrames</c>); 100 ms is a deliberately loose stand-in rather
    /// than widening <see cref="DeviceSessionService"/>'s surface for a timeout estimate.
    /// </summary>
    private const double StimulusLatencySeconds = 0.1;

    /// <summary>
    /// How long one point may take: the time to refill the FFT window after the flush (including
    /// the discarded in-flight audio and the settle time), plus the records the averages cost at
    /// the given hop, times three for slack. Generous on purpose — this exists to notice a wedged
    /// device, not to police timing. <paramref name="captureDelayMs"/> is the user's settle time
    /// (<see cref="DeviceSessionService.CaptureDelayMs"/>), which has to be in the budget or a long
    /// settle reads as a wedged device.
    /// </summary>
        /// <param name="streamSettleMs">
    /// Samples the device will withhold at the start of this acquisition while its hardware
    /// settles (DeviceSessionService.StreamSettleMs). Added to the budget rather than ignored:
    /// those frames are genuinely not coming, and at 8192 points with one average the budget is
    /// 3.3 s against a 3 s settle, so leaving it out turns a settling card into a TimeoutException.
    /// </param>
    public static TimeSpan CaptureTimeout(int fftSize, double sampleRateHz, int averageCount, double hopFraction, double captureDelayMs, double streamSettleMs = 0.0)
    {
        double recordSeconds = fftSize / sampleRateHz;
        double fillSeconds = recordSeconds + StimulusLatencySeconds + (captureDelayMs / 1000.0);

        // Averages are paced by fresh signal, not by the display tick, so the budget is too:
        // averageCount averages at hopFraction cost (1 + (n-1)*hop) records.
        double averageSeconds = (1.0 + ((Math.Max(1, averageCount) - 1) * hopFraction)) * recordSeconds;
        return TimeSpan.FromSeconds(((fillSeconds + averageSeconds) * 3.0) + 2.0 + (streamSettleMs / 1000.0));
    }

    /// <summary>Polls until the pipeline's single shot completes, or <paramref name="timeout"/> passes.</summary>
    /// <exception cref="TimeoutException">The device may have stopped streaming.</exception>
    public static async Task WaitForSingleShotAsync(SpectrumPipeline pipeline, TimeSpan timeout, CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (!pipeline.SingleShotComplete)
        {
            ct.ThrowIfCancellationRequested();

            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException(
                    $"A sweep point did not complete within {timeout.TotalSeconds:0.0} s. The device may have stopped streaming.");
            }

            await Task.Delay(CapturePollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The largest time-domain peak level any measured channel saw, in the same "Pk dBV"
    /// (peak volts / sqrt(2)) convention <c>Qa40xAutoRanger</c> compares against — one input range
    /// serves both channels, so the louder one decides. Negative infinity when nothing was measured.
    ///
    /// <para>The <em>analyzer's</em> peak, not the DUT's: every caller feeds this to
    /// <see cref="InputRangeSelector"/>, whose ladder is in the analyzer's own dBV, so an external
    /// input gain (see <see cref="ExternalGainSetting"/>) is put back here. Every other level a
    /// runner reads stays DUT-referred.</para>
    /// </summary>
    public static double MeasuredPeakDbv(SpectrumSnapshot[] snapshots)
    {
        double best = double.NegativeInfinity;
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Meters is { } meters)
                best = Math.Max(best, meters.AnalyzerPeakDbv);
        }

        return best;
    }

    /// <summary>The loudest fundamental any measured channel reported, or negative infinity when nothing was measured.</summary>
    public static double MeasuredFundamentalDbv(SpectrumSnapshot[] snapshots)
    {
        double best = double.NegativeInfinity;
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Meters is { } meters)
                best = Math.Max(best, meters.FundamentalDbv);
        }

        return best;
    }

    /// <summary>
    /// Mean per-bin Vrms over [loHz, hiHz] of a retained dBV trace — the generator-off residual the
    /// sweeps plot as their noise trace. The snapshot carries dBV per bin, so convert back to volts
    /// first.
    ///
    /// The mean of the amplitudes, matching the previous app, not the RMS of them: for
    /// Rayleigh-distributed noise bins those differ by 1 dB, which is not worth a difference from
    /// the reading this is meant to be comparable with. See <c>ThdSweepRunner.MeasureNoiseFloorAsync</c>
    /// for why it is per bin and not band-integrated.
    /// </summary>
    public static double MeanBandBinVrms(SpectrumSnapshot snapshot, double loHz, double hiHz)
    {
        // Same DC guard as PsdSpectrum.BandOf, from the same window sums -- this is a mean per bin
        // rather than an integral, so one contaminated bin is diluted by the band's length instead
        // of swamping it, but it still lifts the trace and for the same reason.
        int loBin = Math.Max(snapshot.Sums.DcGuardBins, (int)Math.Round(loHz / snapshot.BinSizeHz));
        int hiBin = Math.Min(snapshot.Dbv.Length - 1, (int)Math.Round(hiHz / snapshot.BinSizeHz));
        if (hiBin < loBin)
            return 0.0;

        double sum = 0.0;
        for (int k = loBin; k <= hiBin; k++)
            sum += Math.Pow(10.0, snapshot.Dbv[k] / 20.0);

        return sum / (hiBin - loBin + 1);
    }

    /// <summary>
    /// One sweep point from one channel's meters: the harmonic percentages, the D6+ bucket, the
    /// noise ratio and the power, in the order and the units a stored sweep reads back in.
    ///
    /// <para>Shared rather than written twice because the <i>simulated</i> sweep import does
    /// exactly this to each step of a <c>.step</c>ped transient run, and a second copy of the
    /// harmonic-to-percent mapping would drift from this one silently — the figures would still
    /// look like figures. A measured point and an imported one are now the same arithmetic over the
    /// same metric names, which is what lets the chart, the CSV and the report read them without
    /// knowing which they have.</para>
    /// </summary>
    /// <param name="generatorAmpDbv">
    /// What the gain is measured against. A simulation has no generator this side of the schematic,
    /// so the import passes the fundamental itself, making the gain 0 dB rather than a number
    /// derived from a level that does not exist.
    /// </param>
    /// <param name="noiseVrms">The generator-off residual, or 0 where no noise pass was run — which is every imported point.</param>
    public static ThdSweepPoint PointFromMeters(
        double freqHz,
        int channel,
        SpectrumMeters meters,
        double generatorAmpDbv,
        double loadOhms,
        double noiseVrms,
        SweepPreviewSeries? previewFft,
        SweepPreviewSeries? previewScope)
    {
        var harmonics = meters.Harmonics;
        double fundamentalVrms = Math.Pow(10.0, meters.FundamentalDbv / 20.0);

        var harmonicPercent = new List<double>(4);
        for (int order = 2; order <= 5; order++)
        {
            var match = harmonics.FirstOrDefault(h => h.Order == order);
            if (match.Order != order)
                break;

            harmonicPercent.Add(100.0 * Math.Pow(10.0, match.Dbc / 20.0));
        }

        double d6Plus = harmonics.Length > 0 && harmonics[0].Order == 1
            ? 100.0 * SpectrumMetrics.PartialThd(harmonics, 6)
            : 0.0;

        double noisePercent = fundamentalVrms > 0.0 ? 100.0 * noiseVrms / fundamentalVrms : 0.0;
        double powerWatt = loadOhms > 0.0 ? fundamentalVrms * fundamentalVrms / loadOhms : 0.0;

        return new ThdSweepPoint(
            freqHz,
            channel,
            meters.FundamentalFreqHz,
            meters.FundamentalDbv,
            meters.FundamentalDbv - generatorAmpDbv,
            powerWatt,
            meters.ThdPercent,
            meters.ThdNPercent,
            meters.SnrDb,
            harmonicPercent,
            d6Plus,
            noisePercent,
            generatorAmpDbv,
            previewFft,
            previewScope);
    }

    public static double DbvToVolt(double dbv) => Math.Pow(10.0, dbv / 20.0);

    public static double VoltToDbv(double volt) => 20.0 * Math.Log10(Math.Max(volt, double.Epsilon));
}
