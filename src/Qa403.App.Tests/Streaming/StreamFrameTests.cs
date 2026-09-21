using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Dsp;

namespace Qa403.App.Tests.Streaming;

public class StreamFrameTests
{
    [Fact]
    public void Spectrum_frame_round_trips_header_and_columns()
    {
        float[] freqs = [995f, 1998.4f, 42000f];
        MinMax[] columns = [new(-100.0, -20.0), new(-95.5, -30.25), new(-180.0, -179.9)];

        byte[] frame = StreamFrame.EncodeSpectrum(StreamChannel.Right, seq: 42, x0: 10f, dx: 5.5f, freqs, columns);
        var header = StreamFrame.DecodeHeader(frame);

        Assert.Equal(StreamMsgType.Spectrum, header.MsgType);
        Assert.Equal(StreamChannel.Right, header.Channel);
        Assert.Equal(StreamFlags.Decimated, header.Flags);
        Assert.Equal(42u, header.Seq);
        Assert.Equal(10f, header.X0);
        Assert.Equal(5.5f, header.Dx);
        Assert.Equal((uint)columns.Length, header.NPoints);

        SpectrumColumn[] decoded = StreamFrame.DecodeSpectrumColumns(frame);
        Assert.Equal(columns.Length, decoded.Length);
        for (int i = 0; i < columns.Length; i++)
        {
            Assert.Equal(freqs[i], decoded[i].FreqHz, precision: 2);
            Assert.Equal(columns[i].Min, decoded[i].Value.Min, precision: 4);
            Assert.Equal(columns[i].Max, decoded[i].Value.Max, precision: 4);
        }
    }

    [Fact]
    public void Spectrum_frame_length_matches_header_plus_three_floats_per_point()
    {
        var columns = new MinMax[1600];
        var freqs = new float[1600];
        byte[] frame = StreamFrame.EncodeSpectrum(StreamChannel.Left, seq: 0, x0: 0f, dx: 0f, freqs, columns);

        Assert.Equal(StreamFrame.HeaderLength + (1600 * 3 * sizeof(float)), frame.Length);
    }

    [Fact]
    public void Log_spaced_spectrum_frame_carries_lo_hi_edge_frequencies_in_x0_dx()
    {
        float[] freqs = [12f, 8000f];
        MinMax[] columns = [new(-150.0, -60.0), new(-140.0, -70.0)];

        byte[] frame = StreamFrame.EncodeSpectrum(
            StreamChannel.Left, seq: 3, x0: 10f, dx: 96000f, freqs, columns, logSpaced: true);

        var header = StreamFrame.DecodeHeader(frame);
        Assert.True(header.Flags.HasFlag(StreamFlags.LogSpaced));
        Assert.True(header.Flags.HasFlag(StreamFlags.Decimated));
        Assert.Equal(10f, header.X0);
        Assert.Equal(96000f, header.Dx);
    }

    [Fact]
    public void Scope_frame_uses_requested_msg_type()
    {
        byte[] frame = StreamFrame.EncodeSpectrum(StreamChannel.Left, seq: 1, x0: 0f, dx: 1f, [500f], [new MinMax(-1, 1)], StreamMsgType.Scope);

        Assert.Equal(StreamMsgType.Scope, StreamFrame.DecodeHeader(frame).MsgType);
    }

    [Fact]
    public void EncodeSpectrum_RejectsMismatchedFreqAndColumnLengths()
    {
        Assert.Throws<ArgumentException>(() =>
            StreamFrame.EncodeSpectrum(StreamChannel.Left, seq: 0, x0: 0f, dx: 0f, [1f, 2f], [new MinMax(-1, 1)]));
    }

    [Fact]
    public void Meters_frame_round_trips_scalars_and_harmonics()
    {
        Harmonic[] harmonics =
        [
            new Harmonic(Order: 1, Bin: 1000, AmplitudeVrms: 1.0, AmplitudeDbv: 0.0, Dbc: 0.0),
            new Harmonic(Order: 2, Bin: 2000, AmplitudeVrms: 0.01, AmplitudeDbv: -40.0, Dbc: -40.0),
            new Harmonic(Order: 3, Bin: 3000, AmplitudeVrms: 0.001, AmplitudeDbv: -60.0, Dbc: -60.0),
        ];

        byte[] frame = StreamFrame.EncodeMeters(
            StreamChannel.Left, seq: 7, fundamentalFreqHz: 1000f, binSizeHz: 1.46f,
            thdPercent: 1.001f, thdNPercent: 1.05f, snrDb: 95.5f, fundamentalDbv: -3.01f, noiseFloorDbv: -140f,
            peakDbv: -2.9f, totalRmsDbv: -3.05f,
            harmonics, otherPeaks: []);

        var header = StreamFrame.DecodeHeader(frame);
        Assert.Equal(StreamMsgType.Meters, header.MsgType);
        Assert.Equal(StreamFlags.None, header.Flags);
        Assert.Equal(1000f, header.X0);
        Assert.Equal(1.46f, header.Dx, precision: 4);
        Assert.Equal((uint)harmonics.Length, header.NPoints);

        var meters = StreamFrame.DecodeMeters(frame);
        Assert.Equal(1000f, meters.FundamentalFreqHz);
        Assert.Equal(1.46f, meters.BinSizeHz, precision: 4);
        Assert.Equal(1.001f, meters.ThdPercent, precision: 4);
        Assert.Equal(1.05f, meters.ThdNPercent, precision: 4);
        Assert.Equal(95.5f, meters.SnrDb, precision: 4);
        Assert.Equal(-3.01f, meters.FundamentalDbv, precision: 4);
        Assert.Equal(-140f, meters.NoiseFloorDbv, precision: 4);
        Assert.Equal(-2.9f, meters.PeakDbv, precision: 4);
        Assert.Equal(-3.05f, meters.TotalRmsDbv, precision: 4);

        Assert.Equal(3, meters.Harmonics.Length);
        Assert.Equal(1000f, meters.Harmonics[0].FreqHz);
        Assert.Equal(0f, meters.Harmonics[0].Dbc);
        Assert.Equal(2000f, meters.Harmonics[1].FreqHz);
        Assert.Equal(-40f, meters.Harmonics[1].Dbc);
        Assert.Equal(3000f, meters.Harmonics[2].FreqHz);
        Assert.Equal(-60f, meters.Harmonics[2].Dbc);

        Assert.Empty(meters.OtherPeaks);
        Assert.Equal(0u, meters.AveragesTaken); // default param — existing call sites keep compiling unchanged
    }

    [Fact]
    public void Meters_frame_round_trips_averages_taken()
    {
        Harmonic[] harmonics = [new Harmonic(Order: 1, Bin: 1000, AmplitudeVrms: 1.0, AmplitudeDbv: 0.0, Dbc: 0.0)];

        byte[] frame = StreamFrame.EncodeMeters(
            StreamChannel.Left, seq: 1, fundamentalFreqHz: 1000f, binSizeHz: 1f,
            thdPercent: 0f, thdNPercent: 0f, snrDb: 0f, fundamentalDbv: 0f, noiseFloorDbv: 0f,
            peakDbv: 0f, totalRmsDbv: 0f, harmonics, otherPeaks: [], averagesTaken: 5);

        var meters = StreamFrame.DecodeMeters(frame);
        Assert.Equal(5u, meters.AveragesTaken);
    }

    [Fact]
    public void Meters_frame_round_trips_other_peaks_alongside_harmonics()
    {
        Harmonic[] harmonics = [new Harmonic(Order: 1, Bin: 1000, AmplitudeVrms: 1.0, AmplitudeDbv: 0.0, Dbc: 0.0)];
        (float FreqHz, float Dbc)[] otherPeaks = [(50f, -90f), (100f, -96f), (1234.5f, -55.5f)];

        byte[] frame = StreamFrame.EncodeMeters(
            StreamChannel.Right, seq: 9, fundamentalFreqHz: 1000f, binSizeHz: 1.0f,
            thdPercent: 0f, thdNPercent: 0f, snrDb: 90f, fundamentalDbv: 0f, noiseFloorDbv: -90f,
            peakDbv: 0f, totalRmsDbv: 0f,
            harmonics, otherPeaks);

        var header = StreamFrame.DecodeHeader(frame);
        Assert.Equal(1u, header.NPoints); // still just the harmonic count

        var meters = StreamFrame.DecodeMeters(frame);
        Assert.Single(meters.Harmonics);
        Assert.Equal(3, meters.OtherPeaks.Length);
        Assert.Equal(50f, meters.OtherPeaks[0].FreqHz);
        Assert.Equal(-90f, meters.OtherPeaks[0].Dbc, precision: 3);
        Assert.Equal(100f, meters.OtherPeaks[1].FreqHz);
        Assert.Equal(-96f, meters.OtherPeaks[1].Dbc, precision: 3);
        Assert.Equal(1234.5f, meters.OtherPeaks[2].FreqHz, precision: 1);
        Assert.Equal(-55.5f, meters.OtherPeaks[2].Dbc, precision: 3);
    }

    [Fact]
    public void Decode_throws_on_frame_shorter_than_header()
    {
        Assert.Throws<ArgumentException>(() => StreamFrame.DecodeHeader(new byte[10]));
    }

    [Fact]
    public void SweepPoint_frame_round_trips_previews()
    {
        var previewFft = new SweepPreviewSeries([100f, 200f, 300f], [-90f, -80f, -70f], [-85f, -75f, -65f]);
        var previewScope = new SweepPreviewSeries([0f, 0.0005f, 0.001f], [-1.0f, -0.9f, -1.0f], [1.0f, 0.9f, 1.0f]);
        var point = new ThdSweepPoint(
            FreqHz: 1000.0, Channel: 1, MeasuredFreqHz: 999.8, FundamentalDbv: -20.0, GainDb: -0.1,
            PowerWatt: 0.00125, ThdPercent: 0.05, ThdNPercent: 0.06, SnrDb: 90.0,
            HarmonicPercent: [0.03, 0.01], D6PlusPercent: 0.002, NoisePercent: 0.001,
            GeneratorDbv: -19.9, PreviewFft: previewFft, PreviewScope: previewScope);

        byte[] frame = StreamFrame.EncodeSweepPoint(point, pointIndex: 7);
        var decoded = StreamFrame.DecodeSweepPoint(frame);

        Assert.Equal(1000.0f, decoded.FreqHz, precision: 3);
        Assert.Equal(StreamChannel.Right, decoded.Channel);
        Assert.Equal(7u, decoded.PointIndex);
        Assert.Equal(2, decoded.HarmonicPercent.Length);
        Assert.Equal(-19.9f, decoded.GeneratorDbv, precision: 3);

        Assert.NotNull(decoded.PreviewFft);
        Assert.Equal(previewFft.X, decoded.PreviewFft!.X);
        Assert.Equal(previewFft.Min, decoded.PreviewFft.Min);
        Assert.Equal(previewFft.Max, decoded.PreviewFft.Max);

        Assert.NotNull(decoded.PreviewScope);
        Assert.Equal(previewScope.X, decoded.PreviewScope!.X);
        Assert.Equal(previewScope.Min, decoded.PreviewScope.Min);
        Assert.Equal(previewScope.Max, decoded.PreviewScope.Max);
    }

    /// <summary>
    /// A missing preview (pipeline not asked to capture one) has to decode as null, not as a
    /// zero-length-but-present series — the client tells "no preview for this point" apart from
    /// "an empty one" the same way.
    /// </summary>
    [Fact]
    public void SweepPoint_frame_withNoPreviews_decodesAsNull()
    {
        var point = new ThdSweepPoint(
            FreqHz: 1000.0, Channel: 0, MeasuredFreqHz: 1000.0, FundamentalDbv: -20.0, GainDb: 0.0,
            PowerWatt: 0.0, ThdPercent: 0.0, ThdNPercent: 0.0, SnrDb: 0.0,
            HarmonicPercent: [], D6PlusPercent: 0.0, NoisePercent: 0.0,
            GeneratorDbv: -20.0, PreviewFft: null, PreviewScope: null);

        byte[] frame = StreamFrame.EncodeSweepPoint(point, pointIndex: 0);
        var decoded = StreamFrame.DecodeSweepPoint(frame);

        Assert.Null(decoded.PreviewFft);
        Assert.Null(decoded.PreviewScope);

        // A conventional point (no normalized figures at all) decodes those as null, not as 0 --
        // 0 would read back as a real -inf dBc point on the chart.
        Assert.Null(decoded.NormalizedThdPercent);
        Assert.Null(decoded.NormalizedD6PlusPercent);
        Assert.Equal(4, decoded.NormalizedHarmonicPercent.Length);
        Assert.All(decoded.NormalizedHarmonicPercent, h => Assert.Null(h));
    }

    /// <summary>
    /// The normalized trailer, with the mix of present and absent orders a point in a running
    /// normalized sweep has: its H2 and H3 already referenced, H4 and H5 still waiting.
    /// </summary>
    [Fact]
    public void SweepPoint_frame_round_trips_partlyNormalizedFigures()
    {
        var point = new ThdSweepPoint(
            FreqHz: 1000.0, Channel: 0, MeasuredFreqHz: 1000.0, FundamentalDbv: -20.0, GainDb: 0.0,
            PowerWatt: 0.0, ThdPercent: 0.5, ThdNPercent: 0.6, SnrDb: 90.0,
            HarmonicPercent: [0.4, 0.1, 0.02, 0.01], D6PlusPercent: 0.005, NoisePercent: 0.001,
            GeneratorDbv: -20.0, PreviewFft: null, PreviewScope: null,
            NormalizedThdPercent: 0.52, NormalizedHarmonicPercent: [0.5, 0.12, null, null], NormalizedD6PlusPercent: null);

        byte[] frame = StreamFrame.EncodeSweepPoint(point, pointIndex: 3);
        var decoded = StreamFrame.DecodeSweepPoint(frame);

        Assert.Equal(0.52f, decoded.NormalizedThdPercent!.Value, precision: 5);
        Assert.Null(decoded.NormalizedD6PlusPercent);
        Assert.Equal(0.5f, decoded.NormalizedHarmonicPercent[0]!.Value, precision: 5);
        Assert.Equal(0.12f, decoded.NormalizedHarmonicPercent[1]!.Value, precision: 5);
        Assert.Null(decoded.NormalizedHarmonicPercent[2]);
        Assert.Null(decoded.NormalizedHarmonicPercent[3]);

        // The conventional figures ride along unchanged in the same frame.
        Assert.Equal(0.5f, decoded.ThdPercent, precision: 5);
        Assert.Equal(4, decoded.HarmonicPercent.Length);
    }

    private static FrequencyResponseTrace SampleTrace(FrequencyResponseTraceKind kind = FrequencyResponseTraceKind.Gain) =>
        new(
            kind,
            StartFreqHz: 4.0,
            EndFreqHz: 22800.0,
            FreqHz: [4.0f, 100.0f, 1000.0f],
            MagnitudeMinDb: [-3.5f, -0.2f, -0.1f],
            MagnitudeMaxDb: [-3.0f, 0.1f, 0.2f],
            PhaseDeg: [-12.0f, -1.0f, 0.5f],
            Coherence: [0.4f, 0.99f, 1.0f],
            GroupDelayS: [2.08e-3f, 12.5e-6f, -3.1e-6f],
            PhaseDelayS: [2.10e-3f, 12.4e-6f, 0.0f],
            OneDb: new BandwidthReading(1.0, true, 8.8, 63200.0, 63191.2, false, false),
            ThreeDb: new BandwidthReading(3.0, false, 4.4, 90800.0, 90795.6, false, true),
            PeakDb: 34.49,
            PeakFreqHz: 461.4,
            ReferenceDb: 33.75,
            ReferenceMode: BandwidthReferenceMode.BandMedian,
            ReferenceFellBackToPeak: false);

    [Fact]
    public void FrequencyResponse_RoundTrips()
    {
        var trace = SampleTrace();

        var decoded = StreamFrame.DecodeFrequencyResponse(StreamFrame.EncodeFrequencyResponse(trace));

        Assert.Equal(trace.Kind, decoded.Kind);
        Assert.Equal(trace.FreqHz, decoded.FreqHz);
        Assert.Equal(trace.MagnitudeMinDb, decoded.MagnitudeMinDb);
        Assert.Equal(trace.MagnitudeMaxDb, decoded.MagnitudeMaxDb);
        Assert.Equal(trace.PhaseDeg, decoded.PhaseDeg);
        Assert.Equal(trace.Coherence, decoded.Coherence);
        Assert.Equal(trace.GroupDelayS, decoded.GroupDelayS);
        Assert.Equal(trace.PhaseDelayS, decoded.PhaseDelayS);
        Assert.Equal(trace.StartFreqHz, decoded.StartFreqHz, 3);
        Assert.Equal(trace.EndFreqHz, decoded.EndFreqHz, 3);
        Assert.Equal(trace.PeakDb, decoded.PeakDb, 3);
        Assert.Equal(trace.PeakFreqHz, decoded.PeakFreqHz, 2);
    }

    [Fact]
    public void FrequencyResponse_RejectsRaggedDelayArrays()
    {
        // A column is a fixed stride, so a delay array one short would put every later column's
        // fields in the wrong slots -- better refused at the encoder than decoded as garbage.
        var trace = SampleTrace() with { GroupDelayS = [1e-6f, 2e-6f] };
        Assert.Throws<ArgumentException>(() => StreamFrame.EncodeFrequencyResponse(trace));

        trace = SampleTrace() with { PhaseDelayS = [1e-6f] };
        Assert.Throws<ArgumentException>(() => StreamFrame.EncodeFrequencyResponse(trace));
    }

    [Fact]
    public void FrequencyResponse_RoundTripsTheClampedFlagsSeparatelyPerEnd()
    {
        // The whole point of the flags: "rolls off at 90.8 kHz" and "was still flat where we stopped
        // looking" are different statements, and they can differ between the two ends of one reading.
        var decoded = StreamFrame.DecodeFrequencyResponse(StreamFrame.EncodeFrequencyResponse(SampleTrace()));

        Assert.False(decoded.OneDb.LowerClamped);
        Assert.False(decoded.OneDb.UpperClamped);
        Assert.False(decoded.ThreeDb.LowerClamped);
        Assert.True(decoded.ThreeDb.UpperClamped);

        Assert.Equal(1.0, decoded.OneDb.DropDb, 3);
        Assert.Equal(3.0, decoded.ThreeDb.DropDb, 3);
        Assert.Equal(90800.0, decoded.ThreeDb.UpperHz, 1);
    }

    [Theory]
    [InlineData(FrequencyResponseTraceKind.Left)]
    [InlineData(FrequencyResponseTraceKind.Right)]
    [InlineData(FrequencyResponseTraceKind.Gain)]
    [InlineData(FrequencyResponseTraceKind.CrosstalkLeftToRight)]
    [InlineData(FrequencyResponseTraceKind.CrosstalkRightToLeft)]
    public void FrequencyResponse_CarriesTheTraceKindInTheChannelByte(FrequencyResponseTraceKind kind)
    {
        // Gain belongs to no input channel, which is why the byte holds a trace kind here rather
        // than a StreamChannel -- a client keying off it as left/right would mislabel the gain curve.
        byte[] frame = StreamFrame.EncodeFrequencyResponse(SampleTrace(kind));

        Assert.Equal((byte)kind, frame[1]);
        Assert.Equal(kind, StreamFrame.DecodeFrequencyResponse(frame).Kind);
    }

    [Fact]
    public void FrequencyResponse_RejectsRaggedArrays()
    {
        var trace = SampleTrace() with { Coherence = [1.0f] };
        Assert.Throws<ArgumentException>(() => StreamFrame.EncodeFrequencyResponse(trace));
    }

    [Fact]
    public void FrequencyResponseProgress_RoundTrips()
    {
        byte[] frame = StreamFrame.EncodeFrequencyResponseProgress(3, 8, FrequencyResponsePhase.Measuring, -12.5);

        var decoded = StreamFrame.DecodeFrequencyResponseProgress(frame);

        Assert.Equal(3, decoded.Taken);
        Assert.Equal(8, decoded.Total);
        Assert.Equal(FrequencyResponsePhase.Measuring, decoded.Phase);

        // The level rides on the frame because nothing polls status during a run, and in
        // output-voltage mode the probe is what decides it.
        Assert.Equal(-12.5f, decoded.GeneratorAmpDbv, 3);
    }

    [Fact]
    public void FrequencyResponseProgress_CarriesAZeroLevelFaithfully()
    {
        // 0 dBV is an ordinary drive level, not "absent" — a reader treating it as falsy hides it.
        var decoded = StreamFrame.DecodeFrequencyResponseProgress(
            StreamFrame.EncodeFrequencyResponseProgress(1, 1, FrequencyResponsePhase.Measuring, 0.0));

        Assert.Equal(0.0f, decoded.GeneratorAmpDbv);
    }

    private static IntermodulationResult SampleIntermodulation() => new(
        IntermodulationMethod.Smpte, MaxOrder: 3,
        new ToneMeasurement(10, 0.7071, 1e-12), new ToneMeasurement(700, 0.1768, 1e-12),
        ToneLowFreqHz: 60.0, ToneHighFreqHz: 7000.0, ReferenceVrms: 0.1768, Imd: 0.002,
        [(2, 0.0014), (3, 0.0014)],
        [
            new IntermodulationProduct(2, -1, 1, 694, 6940.0, 1.77e-4, -75.0, -60.0),
            new IntermodulationProduct(2, +1, 1, 706, 7060.0, 1.77e-4, -75.0, -60.0),
            new IntermodulationProduct(3, -1, 2, 688, 6880.0, 1.77e-4, -75.0, -60.0),
            new IntermodulationProduct(3, +1, 2, 712, 7120.0, 1.77e-4, -75.0, -60.0),
        ]);

    [Fact]
    public void Multitone_frame_round_trips_tones_bands_and_the_unresolved_flag()
    {
        var result = new MultitoneResult(
            [new MultitoneTone(100, 1000.0, 0.25, -12.04), new MultitoneTone(104, 1040.0, 0.25, -12.04), new MultitoneTone(200, 2000.0, 0.2, -13.98)],
            [
                new MultitoneBand(0, 104, 100, false, 0, 0.0, double.NegativeInfinity, double.NegativeInfinity, 0.0, double.NegativeInfinity),
                new MultitoneBand(1, 108, 196, true, 150, 1500.0, -72.0, -60.9, 0.0004, -58.9),
            ],
            CombVrms: 0.4062, Tdn: 0.00112, ResolvedBands: 1);

        byte[] frame = StreamFrame.EncodeMultitone(StreamChannel.Right, seq: 9, result, held: true);
        var header = StreamFrame.DecodeHeader(frame);
        var decoded = StreamFrame.DecodeMultitone(frame);

        Assert.Equal(StreamMsgType.Multitone, header.MsgType);
        Assert.Equal(StreamChannel.Right, header.Channel);
        Assert.Equal(9u, header.Seq);
        Assert.Equal(3u, header.NPoints);
        Assert.True(decoded.Held);
        Assert.Equal((float)result.CombDbv, decoded.CombDbv);
        Assert.Equal(0.00112f, decoded.Tdn, 6);
        Assert.Equal(1, decoded.ResolvedBands);
        Assert.Equal(3, decoded.Tones.Length);
        Assert.Equal(1040f, decoded.Tones[1].FreqHz);
        Assert.Equal(-13.98f, decoded.Tones[2].Dbv, 3);
        Assert.Equal(2, decoded.Bands.Length);
        Assert.False(decoded.Bands[0].Resolved);
        Assert.Equal(0f, decoded.Bands[0].HighestDbr);
        Assert.True(decoded.Bands[1].Resolved);
        Assert.Equal(1500f, decoded.Bands[1].HighestFreqHz);
        Assert.Equal(-60.9f, decoded.Bands[1].HighestDbr, 3);
        Assert.Equal(-58.9f, decoded.Bands[1].DnDbr, 3);
        Assert.Equal(-72f, decoded.Bands[1].HighestDbv, 3);
    }

    [Fact]
    public void NoiseFigures_frame_round_trips_figures_enbw_and_weighting()
    {
        var noise = new NoiseFigures(-106.9f, -150.2f, 8.79f, 20f, 20000f, new WeightingSpec(WeightingStandard.ItuR468, User1: false, User2: true));

        byte[] frame = StreamFrame.EncodeNoiseFigures(StreamChannel.Right, seq: 7, noise, held: true);
        var header = StreamFrame.DecodeHeader(frame);
        var decoded = StreamFrame.DecodeNoiseFigures(frame);

        Assert.Equal(StreamMsgType.NoiseFigures, header.MsgType);
        Assert.Equal(StreamChannel.Right, header.Channel);
        Assert.Equal(StreamFlags.Held, header.Flags);
        Assert.Equal(7u, header.Seq);
        Assert.Equal(0u, header.NPoints);
        Assert.Equal(20f, decoded.MeasurementLoFreqHz);
        Assert.Equal(20000f, decoded.MeasurementHiFreqHz);
        Assert.Equal(-106.9f, decoded.BandRmsDbv);
        Assert.Equal(-150.2f, decoded.NoiseDensityDbvPerRootHz);
        Assert.Equal(8.79f, decoded.EnbwHz);
        Assert.Equal(new WeightingSpec(WeightingStandard.ItuR468, false, true), decoded.Weighting);
        Assert.True(decoded.Held);
        Assert.Equal(StreamFrame.HeaderLength + (3 * 4) + 4, frame.Length);
    }

    [Fact]
    public void NoiseFigures_frame_with_no_weighting_decodes_as_none()
    {
        var noise = new NoiseFigures(-100f, -140f, 1f, 20f, 80000f, WeightingSpec.None);
        var decoded = StreamFrame.DecodeNoiseFigures(StreamFrame.EncodeNoiseFigures(StreamChannel.Left, 1, noise, held: false));

        Assert.Equal(WeightingSpec.None, decoded.Weighting);
        Assert.True(decoded.Weighting.IsNone);
        Assert.False(decoded.Held);
    }

    [Fact]
    public void Intermodulation_frame_round_trips_tones_orders_and_products()
    {
        var imd = SampleIntermodulation();

        byte[] frame = StreamFrame.EncodeIntermodulation(StreamChannel.Right, seq: 9, imd, held: false);
        var header = StreamFrame.DecodeHeader(frame);
        var decoded = StreamFrame.DecodeIntermodulation(frame);

        Assert.Equal(StreamMsgType.Intermodulation, header.MsgType);
        Assert.Equal(StreamChannel.Right, header.Channel);
        Assert.Equal(StreamFlags.None, header.Flags);
        Assert.Equal(9u, header.Seq);
        Assert.Equal(4u, header.NPoints);
        Assert.Equal(60f, decoded.ToneLowHz);
        Assert.Equal(7000f, decoded.ToneHighHz);
        Assert.Equal(IntermodulationMethod.Smpte, decoded.Method);
        Assert.Equal(3, decoded.MaxOrder);
        Assert.Equal((float)imd.ToneLowDbv, decoded.ToneLowDbv);
        Assert.Equal((float)imd.ToneHighDbv, decoded.ToneHighDbv);
        Assert.Equal(0.2f, decoded.ImdPercent, precision: 5);
        Assert.Equal((float)imd.SineEquivalentDbv, decoded.SineEquivalentDbv);
        Assert.Equal((float)imd.CombinedRmsDbv, decoded.CombinedRmsDbv);
        Assert.Equal((float)imd.ReferenceDbv, decoded.ReferenceDbv);
        Assert.Equal([(2, 0.14f), (3, 0.14f)], decoded.PerOrder.Select(o => (o.Order, MathF.Round(o.Percent, 4))));
        Assert.Equal(4, decoded.Products.Length);
        Assert.Equal(new DecodedIntermodulationProduct(6880f, -75f, -60f, -1, 2, 3), decoded.Products[2]);
        Assert.False(decoded.Held);
        Assert.Equal(StreamFrame.HeaderLength + 4 + (6 * 4) + 4 + (2 * 8) + (4 * 24), frame.Length);
    }

    [Fact]
    public void Intermodulation_frame_carries_held_flag()
    {
        byte[] frame = StreamFrame.EncodeIntermodulation(StreamChannel.Left, seq: 1, SampleIntermodulation(), held: true);

        Assert.Equal(StreamFlags.Held, StreamFrame.DecodeHeader(frame).Flags);
        Assert.True(StreamFrame.DecodeIntermodulation(frame).Held);
    }

    [Fact]
    public void Intermodulation_unresolved_marker_is_header_only_and_refuses_to_decode()
    {
        byte[] frame = StreamFrame.EncodeIntermodulationUnresolved(StreamChannel.Left, seq: 5);
        var header = StreamFrame.DecodeHeader(frame);

        Assert.Equal(StreamFrame.HeaderLength, frame.Length);
        Assert.Equal(StreamMsgType.Intermodulation, header.MsgType);
        Assert.Equal(StreamFlags.IntermodulationUnresolved, header.Flags);
        Assert.Equal(5u, header.Seq);
        Assert.Equal(0u, header.NPoints);
        Assert.Throws<ArgumentException>(() => StreamFrame.DecodeIntermodulation(frame));
    }

    [Fact]
    public void SweepProgress_frame_carries_the_intermodulation_flag_only_when_asked()
    {
        byte[] plain = StreamFrame.EncodeSweepProgress(3, 10, 0.5, ThdSweepPhase.Sweeping);
        byte[] flagged = StreamFrame.EncodeSweepProgress(3, 10, 0.5, ThdSweepPhase.Sweeping, StreamFlags.IntermodulationSweep);

        Assert.Equal(StreamFlags.None, StreamFrame.DecodeHeader(plain).Flags);
        Assert.Equal(StreamFlags.IntermodulationSweep, StreamFrame.DecodeHeader(flagged).Flags);
        Assert.Equal(plain.AsSpan(4).ToArray(), flagged.AsSpan(4).ToArray());
    }

    [Fact]
    public void ImdSweepPoint_frame_round_trips_orders_and_previews()
    {
        var previewFft = new SweepPreviewSeries([100f, 200f, 300f], [-90f, -80f, -70f], [-85f, -75f, -65f]);
        var point = new ImdSweepPoint(
            Channel: 1, GeneratorSineEquivalentDbv: -20.0, ToneLowDbv: -21.94, ToneHighDbv: -33.98,
            SineEquivalentDbv: -19.98, CombinedRmsDbv: -22.26, GainDb: 0.02, PowerWatt: 0.00125,
            ImdPercent: 0.02, OrderPercent: [0.015, 0.013, 0.002], NoisePercent: 0.001,
            ToneLowHz: 58.59, ToneHighHz: 7001.95, PreviewFft: previewFft, PreviewScope: null);

        byte[] frame = StreamFrame.EncodeImdSweepPoint(point, pointIndex: 4);
        var header = StreamFrame.DecodeHeader(frame);
        var decoded = StreamFrame.DecodeImdSweepPoint(frame);

        Assert.Equal(StreamMsgType.ImdSweepPoint, header.MsgType);
        Assert.Equal(StreamChannel.Right, header.Channel);
        Assert.Equal(4u, header.Seq);
        Assert.Equal(3u, header.NPoints);
        Assert.Equal(58.59f, decoded.ToneLowHz);
        Assert.Equal(7001.95f, decoded.ToneHighHz);
        Assert.Equal(-20f, decoded.GeneratorSineEquivalentDbv);
        Assert.Equal(-21.94f, decoded.ToneLowDbv);
        Assert.Equal(-33.98f, decoded.ToneHighDbv);
        Assert.Equal(-19.98f, decoded.SineEquivalentDbv);
        Assert.Equal(-22.26f, decoded.CombinedRmsDbv);
        Assert.Equal(0.02f, decoded.GainDb);
        Assert.Equal(0.00125f, decoded.PowerWatt);
        Assert.Equal(0.02f, decoded.ImdPercent);
        Assert.Equal(0.001f, decoded.NoisePercent);
        Assert.Equal([0.015f, 0.013f, 0.002f], decoded.OrderPercent);
        Assert.NotNull(decoded.PreviewFft);
        Assert.Equal(previewFft.X, decoded.PreviewFft.X);
        Assert.Equal(previewFft.Max, decoded.PreviewFft.Max);
        Assert.Null(decoded.PreviewScope);
        Assert.Equal(StreamFrame.HeaderLength + (9 * 4) + 4 + (3 * 4) + 4 + (3 * 3 * 4) + 4, frame.Length);
    }
}
