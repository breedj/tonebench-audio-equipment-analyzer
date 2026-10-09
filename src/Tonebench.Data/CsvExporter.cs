using System.Globalization;
using System.Text;

namespace Tonebench.Data;

/// <summary>
/// Renders one measurement result's full-resolution spectra as CSV -- not the decimated display
/// columns <c>MeasurementsEndpoints</c> serves the chart, the actual stored bins. Header comment
/// lines (<c>#</c>-prefixed) carry project, type, name, timestamp, fs, FFT size, per-channel
/// window/S1/S2/ENBW/norm, and the as-run parameters, so the file is interpretable standalone,
/// without the database.
/// </summary>
public static class CsvExporter
{
    public static string Export(MeasurementDetail detail, IReadOnlyList<StoredSpectrum> spectra)
    {
        if (spectra.Count == 0)
            throw new ArgumentException("At least one channel's spectrum is required.", nameof(spectra));

        int binCount = spectra[0].Dbv.Length;
        foreach (var spectrum in spectra)
        {
            if (spectrum.Dbv.Length != binCount)
                throw new ArgumentException("All channels must share the same bin count to align into one CSV.", nameof(spectra));
        }

        var sb = new StringBuilder();
        WriteHeaderComments(sb, detail, spectra);

        sb.Append("freq_hz");
        foreach (var spectrum in spectra)
            sb.Append(",ch").Append(spectrum.Channel).Append("_dbv");
        sb.Append('\n');

        double binSizeHz = spectra[0].BinSizeHz;
        for (int bin = 0; bin < binCount; bin++)
        {
            sb.Append((bin * binSizeHz).ToString("F6", CultureInfo.InvariantCulture));
            foreach (var spectrum in spectra)
                sb.Append(',').Append(spectrum.Dbv[bin].ToString("F4", CultureInfo.InvariantCulture));
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// The seven quantities <c>ThdSweepChart</c> plots, in its own trace order. Each is stored as a
    /// <c>metric</c> row per channel per frequency (see <c>MeasurementSaveService.BuildSweepMetrics</c>),
    /// so a saved sweep exports as exactly what was on screen -- not a re-derivation of it.
    /// </summary>
    private static readonly (string Metric, string Column)[] SweepColumns =
    [
        ("gain_db", "magnitude_db"),
        ("thd_percent", "thd_percent"),
        ("h2_percent", "d2_percent"),
        ("h3_percent", "d3_percent"),
        ("h4_percent", "d4_percent"),
        ("h5_percent", "d5_percent"),
        ("d6plus_percent", "d6plus_percent"),
        ("noise_percent", "noise_percent"),
    ];

    /// <summary>
    /// The frequency-normalized figures (each harmonic over the fundamental at its own frequency),
    /// appended only when the sweep has any -- a conventional sweep's file keeps the columns it
    /// always had. Keyed off <c>nthd_percent</c>, which every normalized point that has a reference
    /// at all carries.
    /// </summary>
    private static readonly (string Metric, string Column)[] NormalizedSweepColumns =
    [
        ("nthd_percent", "nthd_percent"),
        ("nh2_percent", "nd2_percent"),
        ("nh3_percent", "nd3_percent"),
        ("nh4_percent", "nd4_percent"),
        ("nh5_percent", "nd5_percent"),
        ("nd6plus_percent", "nd6plus_percent"),
    ];

    private const string NormalizedMarkerMetric = "nthd_percent";

    /// <summary>
    /// The amplitude axis' column set: the frequency sweep's, plus two columns that only mean
    /// something here -- the level actually reached at the DUT's output, and the power that works
    /// out to. Both are what the chart's own x-axis choices plot, so the CSV can reproduce any of
    /// the three views.
    /// </summary>
    private static readonly (string Metric, string Column)[] AmplitudeSweepColumns =
    [
        ("fundamental_dbv", "output_dbv"),
        ("power_watt", "power_w"),
        ("gain_db", "magnitude_db"),
        ("thd_percent", "thd_percent"),
        ("h2_percent", "d2_percent"),
        ("h3_percent", "d3_percent"),
        ("h4_percent", "d4_percent"),
        ("h5_percent", "d5_percent"),
        ("d6plus_percent", "d6plus_percent"),
        ("noise_percent", "noise_percent"),
    ];

    /// <summary>
    /// The IMD-vs-amplitude column set (see <c>MeasurementSaveService.BuildImdSweepMetrics</c>):
    /// the output on both level conventions, the power, the IMD total and each order to five, the
    /// noise. Selected by <see cref="ImdMarkerMetric"/> being present, which no other sweep writes.
    /// </summary>
    private static readonly (string Metric, string Column)[] ImdSweepColumns =
    [
        ("imd_sine_equivalent_dbv", "output_sine_eq_dbv"),
        ("imd_combined_rms_dbv", "output_rms_dbv"),
        ("power_watt", "power_w"),
        ("gain_db", "magnitude_db"),
        ("imd_percent", "imd_percent"),
        ("im2_percent", "im2_percent"),
        ("im3_percent", "im3_percent"),
        ("im4_percent", "im4_percent"),
        ("im5_percent", "im5_percent"),
        ("noise_percent", "noise_percent"),
    ];

    private const string ImdMarkerMetric = "imd_percent";

    /// <summary>
    /// A frequency-response result as CSV: one row per display column, the curves side by side.
    ///
    /// <para>Its own renderer for the same reason <see cref="ExportSweep"/> is: a response stores
    /// neither spectra nor per-point metrics, so neither of the other two paths has anything to
    /// read. The rows are the decimated display columns rather than raw bins — that is what was
    /// measured *and* what was shown, and a full-resolution export at the finest setting would be a
    /// quarter of a million rows of a curve nobody drew.</para>
    ///
    /// <para>Both magnitude bounds are written per curve, not just the max. A single value would
    /// quietly discard the ripple inside each column, which is the same aggregation bias the
    /// min/max rule exists to prevent on screen.</para>
    /// </summary>
    public static string ExportResponse(MeasurementDetail detail, IReadOnlyList<StoredResponseTrace> traces)
    {
        if (traces.Count == 0)
            throw new ArgumentException("A response with at least one trace is required.", nameof(traces));

        var sb = new StringBuilder();
        WriteResultComments(sb, detail);
        WriteComment(sb, "params_json", detail.ParamsJson);

        // The Measurements table's numbers, as comments: they are properties of a whole curve, not
        // of any one row, and were computed at full resolution before these columns existed.
        foreach (var trace in traces)
        {
            WriteComment(sb, $"{trace.Kind}_peak_db", trace.PeakDb.ToString("F4", CultureInfo.InvariantCulture));
            WriteComment(sb, $"{trace.Kind}_peak_hz", trace.PeakHz.ToString("F4", CultureInfo.InvariantCulture));

            // The level the two corner readings were measured down from, and the rule that produced
            // it. Not derivable from the columns below — a passband-referenced trace's reference is
            // the median of a band, which appears nowhere else in the file — so without these the
            // corners cannot be checked against the curve they came from. Omitted, rather than
            // written empty, for a result saved before it was recorded.
            if (trace.ReferenceDb is { } referenceDb)
                WriteComment(sb, $"{trace.Kind}_reference_db", referenceDb.ToString("F4", CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(trace.ReferenceMode))
                WriteComment(sb, $"{trace.Kind}_reference_mode", trace.ReferenceMode);

            // 1db is a ±1 dB flatness band and 3db a −3 dB roll-off one — see ResponseBandwidth.
            WriteBandwidthComment(sb, trace.Kind, "1db", trace.OneDb);
            WriteBandwidthComment(sb, trace.Kind, "3db", trace.ThreeDb);
        }

        sb.Append("freq_hz");
        foreach (var trace in traces)
        {
            sb.Append(',').Append(trace.Kind).Append("_min_db");
            sb.Append(',').Append(trace.Kind).Append("_max_db");
            sb.Append(',').Append(trace.Kind).Append("_phase_deg");
            sb.Append(',').Append(trace.Kind).Append("_coherence");

            // Microseconds rather than the stored seconds: a spreadsheet reading 2.083e-03 next to
            // 1.2e-05 is unreadable, and every delay an audio DUT has fits in whole microseconds.
            // Omitted for a result saved before the delays were recorded, the reference_db policy.
            if (HasDelays(trace))
            {
                sb.Append(',').Append(trace.Kind).Append("_group_delay_us");
                sb.Append(',').Append(trace.Kind).Append("_phase_delay_us");
            }
        }

        sb.Append('\n');

        // Every curve comes from one measurement and so shares a column grid; the shortest is used
        // defensively rather than assumed, since a ragged row would silently shift columns.
        int rows = traces.Min(t => t.FreqHz.Length);
        for (int i = 0; i < rows; i++)
        {
            sb.Append(traces[0].FreqHz[i].ToString("F6", CultureInfo.InvariantCulture));
            foreach (var trace in traces)
            {
                sb.Append(',').Append(trace.MagnitudeMinDb[i].ToString("F4", CultureInfo.InvariantCulture));
                sb.Append(',').Append(trace.MagnitudeMaxDb[i].ToString("F4", CultureInfo.InvariantCulture));
                sb.Append(',').Append(trace.PhaseDeg[i].ToString("F4", CultureInfo.InvariantCulture));
                sb.Append(',').Append(trace.Coherence[i].ToString("F6", CultureInfo.InvariantCulture));

                if (HasDelays(trace))
                {
                    sb.Append(',').Append((trace.GroupDelayS![i] * 1e6).ToString("F3", CultureInfo.InvariantCulture));
                    sb.Append(',').Append((trace.PhaseDelayS![i] * 1e6).ToString("F3", CultureInfo.InvariantCulture));
                }
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Whether a stored trace carries delay curves at all — a row from before migration 012 does not.</summary>
    private static bool HasDelays(StoredResponseTrace trace) =>
        trace.GroupDelayS is not null && trace.PhaseDelayS is not null
        && trace.GroupDelayS.Length == trace.FreqHz.Length && trace.PhaseDelayS.Length == trace.FreqHz.Length;

    /// <summary>
    /// An impedance result as CSV: one row per display column, in <b>ohms</b>.
    ///
    /// <para>Its own renderer rather than a branch inside <see cref="ExportResponse"/> because the
    /// units are different all the way down. Impedance rides in the same <c>response_trace</c> rows,
    /// but its magnitude columns hold dB relative to 1 ohm rather than a gain, and its bandwidth
    /// columns hold nothing at all -- a -3 dB corner is a response concept. Writing dB into a column
    /// headed <c>_db</c> would be true and useless: nobody wants a loudspeaker's impedance in
    /// decibels.</para>
    ///
    /// <para><b>Resistance and reactance are written out alongside</b>, from the same magnitude and
    /// phase. They are not extra information -- <c>R = |Z| cos(phi)</c>, <c>X = |Z| sin(phi)</c> --
    /// but they are the form the numbers are actually used in, and recomputing them in a spreadsheet
    /// is where a degrees/radians mistake gets made.</para>
    ///
    /// <para>A calibration standard's curve is a ratio rather than an impedance, so its columns are
    /// headed <c>_db</c> and it gets no R and X. The kind prefix on every column is what keeps the
    /// two readable in one table.</para>
    /// </summary>
    public static string ExportImpedance(MeasurementDetail detail, IReadOnlyList<StoredResponseTrace> traces)
    {
        if (traces.Count == 0)
            throw new ArgumentException("An impedance result with at least one trace is required.", nameof(traces));

        var sb = new StringBuilder();
        WriteResultComments(sb, detail);

        // Carries the reference resistor and both calibrations, without which none of these numbers
        // can be reproduced.
        WriteComment(sb, "params_json", detail.ParamsJson);

        foreach (var trace in traces)
        {
            if (IsImpedanceCurve(trace.Kind))
            {
                WriteComment(sb, $"{trace.Kind}_peak_ohms", ToOhms(trace.PeakDb).ToString("F6", CultureInfo.InvariantCulture));
                WriteComment(sb, $"{trace.Kind}_peak_hz", trace.PeakHz.ToString("F4", CultureInfo.InvariantCulture));
            }
            else
            {
                WriteComment(sb, $"{trace.Kind}_peak_db", trace.PeakDb.ToString("F4", CultureInfo.InvariantCulture));
                WriteComment(sb, $"{trace.Kind}_peak_hz", trace.PeakHz.ToString("F4", CultureInfo.InvariantCulture));
            }
        }

        sb.Append("freq_hz");
        foreach (var trace in traces)
        {
            if (IsImpedanceCurve(trace.Kind))
            {
                sb.Append(',').Append(trace.Kind).Append("_min_ohms");
                sb.Append(',').Append(trace.Kind).Append("_max_ohms");
                sb.Append(',').Append(trace.Kind).Append("_phase_deg");
                sb.Append(',').Append(trace.Kind).Append("_resistance_ohms");
                sb.Append(',').Append(trace.Kind).Append("_reactance_ohms");
                sb.Append(',').Append(trace.Kind).Append("_coherence");
            }
            else
            {
                sb.Append(',').Append(trace.Kind).Append("_min_db");
                sb.Append(',').Append(trace.Kind).Append("_max_db");
                sb.Append(',').Append(trace.Kind).Append("_phase_deg");
                sb.Append(',').Append(trace.Kind).Append("_coherence");
            }
        }

        sb.Append('\n');

        // Every curve comes from one measurement and so shares a column grid; the shortest is used
        // defensively rather than assumed, since a ragged row would silently shift columns.
        int rows = traces.Min(t => t.FreqHz.Length);
        for (int i = 0; i < rows; i++)
        {
            sb.Append(traces[0].FreqHz[i].ToString("F6", CultureInfo.InvariantCulture));
            foreach (var trace in traces)
            {
                if (IsImpedanceCurve(trace.Kind))
                {
                    double min = ToOhms(trace.MagnitudeMinDb[i]);
                    double max = ToOhms(trace.MagnitudeMaxDb[i]);
                    double phaseDeg = trace.PhaseDeg[i];

                    // R and X from the column's midpoint, which is what a reader takes off the chart:
                    // the max alone carries the aggregation bias of a max over several bins.
                    double magnitude = 0.5 * (min + max);
                    double radians = phaseDeg * Math.PI / 180.0;

                    sb.Append(',').Append(min.ToString("F6", CultureInfo.InvariantCulture));
                    sb.Append(',').Append(max.ToString("F6", CultureInfo.InvariantCulture));
                    sb.Append(',').Append(phaseDeg.ToString("F4", CultureInfo.InvariantCulture));
                    sb.Append(',').Append((magnitude * Math.Cos(radians)).ToString("F6", CultureInfo.InvariantCulture));
                    sb.Append(',').Append((magnitude * Math.Sin(radians)).ToString("F6", CultureInfo.InvariantCulture));
                    sb.Append(',').Append(trace.Coherence[i].ToString("F6", CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append(',').Append(trace.MagnitudeMinDb[i].ToString("F4", CultureInfo.InvariantCulture));
                    sb.Append(',').Append(trace.MagnitudeMaxDb[i].ToString("F4", CultureInfo.InvariantCulture));
                    sb.Append(',').Append(trace.PhaseDeg[i].ToString("F4", CultureInfo.InvariantCulture));
                    sb.Append(',').Append(trace.Coherence[i].ToString("F6", CultureInfo.InvariantCulture));
                }
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Whether a stored trace holds ohms rather than a plain ratio.</summary>
    private static bool IsImpedanceCurve(string kind) => kind is ResponseTraceKind.Impedance or ResponseTraceKind.OutputImpedance;

    private static double ToOhms(double dbOhms) => Math.Pow(10.0, dbOhms / 20.0);

    /// <summary>
    /// One corner reading. A clamped end is written as an empty field rather than the frequency the
    /// search stopped at: the curve never reached the threshold there, so there is no corner, and a
    /// number would read as a measured one.
    /// </summary>
    private static void WriteBandwidthComment(StringBuilder sb, string kind, string drop, ResponseBandwidth bandwidth)
    {
        WriteComment(
            sb,
            $"{kind}_{drop}_lower_hz",
            bandwidth.LowerClamped ? "" : bandwidth.LowerHz.ToString("F4", CultureInfo.InvariantCulture));
        WriteComment(
            sb,
            $"{kind}_{drop}_upper_hz",
            bandwidth.UpperClamped ? "" : bandwidth.UpperHz.ToString("F4", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A swept-sine impulse response's own samples as CSV: one row per sample, in volts.
    ///
    /// <para>Its own renderer, and its own <c>part</c> of the export, because an impulse-response
    /// result holds <b>three different grids</b> and no single table can carry them: these samples
    /// against time, the gated magnitude/phase curves against frequency (<c>response_trace</c> rows,
    /// rendered by <see cref="ExportResponse"/>) and the harmonic curves against fundamental
    /// frequency (<see cref="ExportImpulseHarmonics"/>). Writing one row that meant a time in one
    /// column group and a frequency in another is the thing <see cref="ExportImpedance"/> refused to
    /// do with units, for the same reason.</para>
    ///
    /// <para>This is the one <b>full-resolution</b> thing an impulse result stores — up to
    /// <c>MeasurementSaveService.MaxStoredIrSamples</c> samples, not a decimated display curve — so
    /// it is what the default part exports: the frequency curves can be re-derived from these
    /// samples, and these samples cannot be re-derived from anything else.</para>
    ///
    /// <para><b><c>time_ms</c> is relative to the peak</b>, so a band-limited impulse's pre-ringing
    /// reads as negative time rather than as the end of the record. That is what
    /// <c>impulse_result.peak_index</c> is stored for; a reader that assumed index 0 was the peak
    /// would report the pre-ringing as post-ringing. Milliseconds rather than the stored seconds for
    /// the reason the response export writes delays in microseconds: every impulse an audio DUT has
    /// fits in a few tens of milliseconds, and a spreadsheet column of 2.08e-05 is unreadable.</para>
    /// </summary>
    public static string ExportImpulse(MeasurementDetail detail, StoredImpulseResult impulse)
    {
        if (impulse.Impulses.Count == 0)
            throw new ArgumentException("An impulse result with at least one impulse is required.", nameof(impulse));

        var impulses = impulse.Impulses;
        var sb = new StringBuilder();
        WriteResultComments(sb, detail);

        foreach (var trace in impulses)
        {
            WriteComment(sb, $"{trace.Kind}_peak_volts", trace.PeakVolts.ToString("F6", CultureInfo.InvariantCulture));
            WriteComment(sb, $"{trace.Kind}_peak_index", trace.PeakIndex.ToString(CultureInfo.InvariantCulture));
            WriteComment(sb, $"{trace.Kind}_ir_samples", trace.Ir.Length.ToString(CultureInfo.InvariantCulture));
            WriteComment(sb, $"{trace.Kind}_sample_rate_hz", trace.SampleRateHz.ToString(CultureInfo.InvariantCulture));
            WriteComment(sb, $"{trace.Kind}_gate_pre_ms", trace.GatePreMs.ToString("F4", CultureInfo.InvariantCulture));
            WriteComment(sb, $"{trace.Kind}_gate_post_ms", trace.GatePostMs.ToString("F4", CultureInfo.InvariantCulture));
            WriteComment(sb, $"{trace.Kind}_gate_window", trace.GateWindow);

            // The finest frequency the gate leaves meaningful — a property of the whole curve, like
            // the response export's corners, and not derivable from the samples below.
            WriteComment(sb, $"{trace.Kind}_resolution_limit_hz", trace.ResolutionLimitHz.ToString("F4", CultureInfo.InvariantCulture));
            WriteComment(sb, $"{trace.Kind}_reference_mode", trace.ReferenceMode);
            if (!string.IsNullOrEmpty(trace.ReferenceName))
                WriteComment(sb, $"{trace.Kind}_reference_name", trace.ReferenceName);

            // Omitted rather than written empty where there was no measured reference to remove a
            // delay against — the reference_db policy. See FrequencyResponseEstimator: against the
            // synthetic chirp the rotation is the record's arbitrary start point, not a delay.
            if (trace.LatencySamples is { } latency)
                WriteComment(sb, $"{trace.Kind}_latency_samples", latency.ToString("F4", CultureInfo.InvariantCulture));
        }

        // Every impulse of one result is centred the same way by MeasurementSaveService and shares
        // the acquisition's sample rate, so one time column serves them all. Said out loud rather
        // than assumed: a disagreement would put every row of the odd one out at the wrong time, and
        // the comments above are the only other place a reader could notice.
        var reference = impulses[0];
        foreach (var trace in impulses.Skip(1))
        {
            if (trace.PeakIndex != reference.PeakIndex || trace.SampleRateHz != reference.SampleRateHz)
            {
                WriteComment(
                    sb,
                    "warning",
                    $"{trace.Kind} is centred at sample {trace.PeakIndex.ToString(CultureInfo.InvariantCulture)} at "
                        + $"{trace.SampleRateHz.ToString(CultureInfo.InvariantCulture)} Hz, not {reference.PeakIndex.ToString(CultureInfo.InvariantCulture)} at "
                        + $"{reference.SampleRateHz.ToString(CultureInfo.InvariantCulture)} Hz — its samples are not on the time column below");
            }
        }

        WriteComment(sb, "params_json", detail.ParamsJson);

        sb.Append("time_ms");
        foreach (var trace in impulses)
            sb.Append(',').Append(trace.Kind).Append("_volts");
        sb.Append('\n');

        // The shortest, defensively, the way ExportResponse treats its curves: a ragged row would
        // silently shift columns.
        int rows = impulses.Min(t => t.Ir.Length);
        double sampleRateHz = reference.SampleRateHz > 0.0 ? reference.SampleRateHz : 1.0;

        for (int i = 0; i < rows; i++)
        {
            double timeMs = (i - reference.PeakIndex) / sampleRateHz * 1000.0;
            sb.Append(timeMs.ToString("F6", CultureInfo.InvariantCulture));
            foreach (var trace in impulses)
                sb.Append(',').Append(trace.Ir[i].ToString("F9", CultureInfo.InvariantCulture));

            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// The harmonic-distortion curves a swept-sine impulse response extracts, as CSV: one row per
    /// (order, point).
    ///
    /// <para><b>Long format, not a column per order</b>, because each order carries its own
    /// frequency grid: the k-th harmonic can only be observed for fundamentals up to the sweep's end
    /// over k, so the curves are genuinely different lengths and end at different frequencies
    /// (<c>impulse_harmonic.upper_valid_hz</c>). Side-by-side columns would need the shortest grid
    /// and would throw away the top of every lower order, or pad with empties that read as missing
    /// data rather than as out-of-range.</para>
    ///
    /// <para><c>level_dbc</c> is relative to the fundamental at that frequency, the same convention
    /// the harmonics chart plots.</para>
    /// </summary>
    public static string ExportImpulseHarmonics(MeasurementDetail detail, StoredImpulseResult impulse)
    {
        if (impulse.Harmonics.Count == 0)
            throw new ArgumentException("An impulse result with at least one harmonic curve is required.", nameof(impulse));

        var curves = impulse.Harmonics.OrderBy(c => c.Order).ToList();
        var sb = new StringBuilder();
        WriteResultComments(sb, detail);

        foreach (var curve in curves)
        {
            WriteComment(
                sb,
                $"h{curve.Order.ToString(CultureInfo.InvariantCulture)}_upper_valid_hz",
                curve.UpperValidHz.ToString("F4", CultureInfo.InvariantCulture));
        }

        WriteComment(sb, "params_json", detail.ParamsJson);

        sb.Append("order,fundamental_hz,level_dbc\n");

        foreach (var curve in curves)
        {
            // The shortest of the pair, defensively: the two blobs are written together and so
            // cannot normally disagree, but a row that ran off the end of one would be a silent
            // reading of whatever followed it.
            int rows = Math.Min(curve.FundamentalHz.Length, curve.LevelDbc.Length);
            for (int i = 0; i < rows; i++)
            {
                sb.Append(curve.Order.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(curve.FundamentalHz[i].ToString("F6", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(curve.LevelDbc[i].ToString("F4", CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Renders a saved sweep as CSV: one row per point, one column group per channel. A sweep
    /// result has no <c>spectrum</c> rows at all (metrics only, by design -- see
    /// <see cref="SweepSaveRequest"/>), so <see cref="Export"/> cannot render one and this is the
    /// separate path rather than a branch inside it.
    ///
    /// The leading column is the sweep's own axis: <c>freq_hz</c> for THD vs frequency,
    /// <c>generator_volt</c> for THD vs amplitude -- which is also the only thing that identifies a
    /// row there, since every point of an amplitude sweep shares one frequency.
    ///
    /// A metric missing for a channel at one point is written as an empty field, not 0: a
    /// harmonic above Nyquist is genuinely absent there (<c>BuildSweepMetrics</c> stops at the
    /// highest order actually found), and a 0 would plot as a real -infinity reading.
    /// </summary>
    public static string ExportSweep(MeasurementDetail detail, IReadOnlyList<StoredSweepPoint> points)
    {
        if (points.Count == 0)
            throw new ArgumentException("A sweep with at least one point is required.", nameof(points));

        var channels = points
            .SelectMany(p => p.MetricsByChannel.Keys)
            .Distinct()
            .Order()
            .ToList();

        // Told apart by the level *varying*, the same rule MeasurementRepository.SelectHeadlineCapture
        // applies -- not by the metric being present. Every sweep writes generator_volt on every
        // point (it is the level the point was played at), so presence marked every frequency sweep
        // saved since the amplitude axis existed as level-stepped: a constant 0.1 V leading column
        // and no frequency column at all.
        bool amplitudeAxis = points
            .SelectMany(p => p.MetricsByChannel.Values)
            .Where(m => m.ContainsKey(AmplitudeAxisMetric))
            .Select(m => m[AmplitudeAxisMetric])
            .Distinct()
            .Count() > 1;
        bool normalized = points.Any(p => p.MetricsByChannel.Values.Any(m => m.ContainsKey(NormalizedMarkerMetric)));
        bool imd = points.Any(p => p.MetricsByChannel.Values.Any(m => m.ContainsKey(ImdMarkerMetric)));
        var columns = imd ? ImdSweepColumns : amplitudeAxis ? AmplitudeSweepColumns : SweepColumns;
        if (normalized)
            columns = [.. columns, .. NormalizedSweepColumns];

        var sb = new StringBuilder();
        WriteResultComments(sb, detail);
        WriteComment(sb, "point_count", points.Count.ToString(CultureInfo.InvariantCulture));
        WriteComment(sb, "params_json", detail.ParamsJson);

        if (imd)
        {
            // Both tones, from the first point that has them; the leading column is the sine-equivalent
            // generator level in volts, as for the THD amplitude sweep.
            var first = points[0].MetricsByChannel.Values.FirstOrDefault(m => m.ContainsKey("imd_tone_high_hz"));
            if (first is not null)
            {
                WriteComment(sb, "tone_low_hz", first["imd_tone_low_hz"].ToString("F6", CultureInfo.InvariantCulture));
                WriteComment(sb, "tone_high_hz", first["imd_tone_high_hz"].ToString("F6", CultureInfo.InvariantCulture));
            }
        }
        else if (amplitudeAxis)
        {
            WriteComment(sb, "freq_hz", points[0].FreqHz.ToString("F6", CultureInfo.InvariantCulture));
        }

        sb.Append(amplitudeAxis ? "generator_volt" : "freq_hz");
        foreach (int channel in channels)
            foreach (var (_, column) in columns)
                sb.Append(",ch").Append(channel).Append('_').Append(column);
        sb.Append('\n');

        foreach (var point in points.OrderBy(p => p.SeqIndex))
        {
            double x = amplitudeAxis ? AmplitudeOf(point) : point.FreqHz;
            sb.Append(x.ToString("F6", CultureInfo.InvariantCulture));
            foreach (int channel in channels)
            {
                point.MetricsByChannel.TryGetValue(channel, out var metrics);
                foreach (var (metric, _) in columns)
                {
                    sb.Append(',');
                    if (metrics is not null && metrics.TryGetValue(metric, out double value))
                        sb.Append(value.ToString("F6", CultureInfo.InvariantCulture));
                }
            }
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>The metric whose value moving from point to point marks a sweep as level-stepped rather than frequency-stepped.</summary>
    private const string AmplitudeAxisMetric = "generator_volt";

    /// <summary>
    /// A point's generator level in volts. Written per channel but identical across them (it is one
    /// stimulus), so the first channel that has it answers for the point.
    /// </summary>
    private static double AmplitudeOf(StoredSweepPoint point)
    {
        foreach (var metrics in point.MetricsByChannel.Values)
            if (metrics.TryGetValue(AmplitudeAxisMetric, out double volt))
                return volt;

        return double.NaN;
    }

    private static void WriteHeaderComments(StringBuilder sb, MeasurementDetail detail, IReadOnlyList<StoredSpectrum> spectra)
    {
        WriteComment(sb, "project", detail.ProjectName);
        WriteComment(sb, "measurement_type", detail.MeasurementTypeName);
        WriteComment(sb, "name", detail.Name);
        WriteComment(sb, "notes", detail.Notes ?? "");
        WriteComment(sb, "timestamp_utc", TonebenchTimestamp.FormatUtc(detail.TimestampUtc));
        WriteComment(sb, "sample_rate_hz", detail.SampleRateHz.ToString(CultureInfo.InvariantCulture));
        WriteComment(sb, "fft_size", detail.FftSize.ToString(CultureInfo.InvariantCulture));
        WriteComment(sb, "params_json", detail.ParamsJson);

        foreach (var spectrum in spectra)
        {
            string norm = spectrum.Norm == SpectrumNorm.Amplitude ? "amplitude" : "psd";
            WriteComment(
                sb,
                $"channel_{spectrum.Channel}",
                $"window={spectrum.Window} s1={spectrum.S1.ToString(CultureInfo.InvariantCulture)} "
                    + $"s2={spectrum.S2.ToString(CultureInfo.InvariantCulture)} enbw_hz={spectrum.EnbwHz.ToString(CultureInfo.InvariantCulture)} "
                    + $"norm={norm}");
        }
    }

    /// <summary>
    /// The identifying header comments every non-FFT renderer opens with, in one place so the four
    /// of them cannot drift apart. <c>params_json</c> is deliberately <b>not</b> here: each renderer
    /// writes its own extras between these lines and it, and the sweep's <c>point_count</c> sits in
    /// exactly that gap.
    /// </summary>
    private static void WriteResultComments(StringBuilder sb, MeasurementDetail detail)
    {
        WriteComment(sb, "project", detail.ProjectName);
        WriteComment(sb, "measurement_type", detail.MeasurementTypeName);
        WriteComment(sb, "kind", detail.Kind);
        WriteComment(sb, "name", detail.Name);
        WriteComment(sb, "notes", detail.Notes ?? "");
        WriteComment(sb, "timestamp_utc", TonebenchTimestamp.FormatUtc(detail.TimestampUtc));
        WriteComment(sb, "sample_rate_hz", detail.SampleRateHz.ToString(CultureInfo.InvariantCulture));
        WriteComment(sb, "fft_size", detail.FftSize.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteComment(StringBuilder sb, string key, string value)
    {
        // '#'-prefixed, never parsed as a delimited field -- newline stripping is enough escaping.
        sb.Append("# ").Append(key).Append(": ").Append(value.Replace('\r', ' ').Replace('\n', ' ')).Append('\n');
    }
}
