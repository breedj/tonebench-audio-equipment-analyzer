using System.Text.Json;
using Tonebench.App.Streaming;
using Tonebench.Data;
using Tonebench.Device;
using Tonebench.Dsp;

namespace Tonebench.App.Services;

/// <summary>
/// Statistics of a simulator's time axis — what the import needs in order to say whether a file can
/// be analysed at all, and at what rate.
/// </summary>
public sealed record TimeAxis(
    double StartS,
    double EndS,
    double MinStepS,
    double MedianStepS,
    double MaxStepS,
    bool IsMonotonic,
    int FirstNonMonotonicIndex)
{
    public double DurationS => EndS - StartS;

    /// <summary>
    /// The uniform rate that represents this axis without inventing anything: the reciprocal of the
    /// <i>median</i> step. Median rather than mean because a simulator takes a handful of very
    /// short steps at a discontinuity and the mean follows them; and rather than the minimum,
    /// which on the reference run is 0.1 µs against a median of 1 µs and would make the stored
    /// waveform ten times larger for nothing.
    /// </summary>
    public double NativeRateHz => 1.0 / Math.Max(MedianStepS, double.Epsilon);

    /// <summary>
    /// How much finer than the <i>coarsest</i> step the uniform regrid is allowed to be.
    ///
    /// <para>This bound is the difference between working and falling over. A simulator left to
    /// choose its own steps takes picosecond ones through a switching edge and tens of microseconds
    /// through a quiet stretch: measured on a QSpice run of an audio amplifier, 6.25 ps median
    /// against a 42.5 us worst case, a spread of seven orders of magnitude. Regridding at
    /// 1/median there is 160 GHz, i.e. sixteen billion samples for a tenth of a second — an
    /// allocation that cannot succeed, for detail a hundred thousand times above the audio band.
    /// Sixty-four times the coarsest step is far more than any audio measurement can use and is
    /// bounded by construction.</para>
    /// </summary>
    private const double MaxOversampleOfCoarsestStep = 64.0;

    /// <summary>
    /// The uniform rate to resample onto: the median step's rate, never coarser than four times
    /// the top of the measurement band and never finer than
    /// <see cref="MaxOversampleOfCoarsestStep"/> times the coarsest step actually taken.
    ///
    /// <para>The two bounds cannot cross for a file worth importing: the coarse-step refusal
    /// already requires the worst step to carry the band, which puts the ceiling at least 128x the
    /// band top while the floor is 4x.</para>
    /// </summary>
    public double ResampleRateHz(double measurementHiFreqHz)
    {
        double fromMedian = NativeRateHz;
        double ceiling = MaxOversampleOfCoarsestStep / Math.Max(MaxStepS, double.Epsilon);
        double floor = 4.0 * Math.Max(measurementHiFreqHz, 1.0);
        return Math.Clamp(fromMedian, Math.Min(floor, ceiling), Math.Max(floor, ceiling));
    }

    /// <summary>True when the steps vary so much that the regrid rate had to be bounded.</summary>
    public bool IsAdaptive(double measurementHiFreqHz) => ResampleRateHz(measurementHiFreqHz) < NativeRateHz * 0.999;

    public static TimeAxis Of(ReadOnlySpan<double> timeS)
    {
        if (timeS.Length < 2)
            throw new ArgumentException("A time axis needs at least two points.", nameof(timeS));

        var steps = new double[timeS.Length - 1];
        bool monotonic = true;
        int firstBad = -1;

        for (int i = 1; i < timeS.Length; i++)
        {
            steps[i - 1] = timeS[i] - timeS[i - 1];
            if (monotonic && steps[i - 1] <= 0.0)
            {
                monotonic = false;
                firstBad = i;
            }
        }

        var sorted = (double[])steps.Clone();
        Array.Sort(sorted);

        return new TimeAxis(
            StartS: timeS[0],
            EndS: timeS[^1],
            MinStepS: sorted[0],
            MedianStepS: sorted[sorted.Length / 2],
            MaxStepS: sorted[^1],
            IsMonotonic: monotonic,
            FirstNonMonotonicIndex: firstBad);
    }
}

/// <summary>
/// Everything about how a stored simulation is analysed, and how that travels in
/// <c>params_json</c>. All of it is re-appliable: change any field and
/// <see cref="SimulationImportService.ReanalyzeAsync"/> rebuilds the spectrum from the stored
/// samples.
///
/// <para><b>The one thing to understand about the FFT size here.</b> The analysis rate is derived
/// from the run's duration — <c>fs = fftSize / usableSeconds</c> — so the record always spans the
/// whole simulation and the bin width is always <c>1 / usableSeconds</c>, <i>whatever the FFT size
/// is</i>. A bigger FFT therefore buys no resolution at all; it raises the analysis rate and so
/// computes more empty spectrum above the audio band. Resolution comes only from simulating for
/// longer: 95 ms gives 10.5 Hz bins, and matching the bench's 2.93 Hz needs about 341 ms saved.
/// <see cref="SuggestedFftSize"/> is the smallest size whose Nyquist clears the measurement band,
/// which is the one worth using.</para>
/// </summary>
public sealed record SimulationAnalysis(
    SimulationSource Source,
    double StartTimeS,
    double LevelOffsetDb,
    int FftSize,
    WindowType Window,
    double MeasurementLoFreqHz,
    double MeasurementHiFreqHz,
    bool PeriodicRecord,
    IReadOnlyList<double>? RemovedDcV = null,
    double? PcmStartS = null)
{
    /// <summary>
    /// Headroom over the measurement band that <see cref="SuggestedFftSize"/> leaves, so the band's
    /// top is comfortably inside the analysis rate's Nyquist rather than against it.
    /// </summary>
    private const double NyquistHeadroom = 1.2;

    public static int MinFftSize => 1024;

    public static int MaxFftSize => 1 << 20;

    /// <summary>
    /// The largest FFT size this run can support: anything beyond the samples it actually holds
    /// would be interpolating past the simulator's own resolution rather than measuring it.
    /// </summary>
    public static int LargestFftSize(double durationS, double nativeRateHz)
    {
        double samples = durationS * nativeRateHz;
        int size = MinFftSize;
        while ((long)size * 2 <= samples && size < MaxFftSize)
            size *= 2;
        return size;
    }

    /// <summary>
    /// The smallest FFT size whose derived Nyquist clears
    /// <paramref name="measurementHiFreqHz"/> with <see cref="NyquistHeadroom"/> to spare. See the
    /// type's own remarks for why bigger is not better here.
    /// </summary>
    public static int SuggestedFftSize(double usableS, double measurementHiFreqHz, double nativeRateHz)
    {
        double wantedRate = 2.0 * NyquistHeadroom * measurementHiFreqHz;
        double wantedSize = usableS * wantedRate;

        int size = MinFftSize;
        while (size < wantedSize && size < MaxFftSize)
            size *= 2;

        return Math.Min(size, LargestFftSize(usableS, nativeRateHz));
    }

    /// <summary>How many samples of the simulation this analysis uses.</summary>
    public double UsableS => Math.Max(0.0, Source.SourceEndS - StartTimeS);

    /// <summary>
    /// Where the stored samples begin, in absolute simulation time -- see
    /// <see cref="SimulationParameters.PcmStartS"/>. Equal to <see cref="StartTimeS"/> for a result
    /// that has not been re-analysed at a different start, and for every row written before it was
    /// recorded.
    /// </summary>
    public double SamplesStartS => PcmStartS ?? StartTimeS;

    /// <summary>
    /// How far into the stored samples this analysis starts, in samples at their own rate. Zero at
    /// import; non-zero only once a re-analysis has moved the start forward.
    /// </summary>
    public int SampleOffset(double pcmRateHz) => pcmRateHz > 0.0
        ? Math.Max(0, (int)Math.Round((StartTimeS - SamplesStartS) * pcmRateHz))
        : 0;

    /// <summary>
    /// The bin width the result will have, which depends on the duration alone. Quoted to the user
    /// because it is the one number a longer simulation would improve.
    /// </summary>
    public double BinWidthHz => UsableS > 0.0 ? 1.0 / UsableS : 0.0;

    public double BaseAnalysisRateHz => UsableS > 0.0 ? FftSize / UsableS : 0.0;

    /// <summary>
    /// The rate to analyse at, given a fundamental that a first pass has already found.
    ///
    /// <para>With <see cref="PeriodicRecord"/> on, the record is shortened by up to one period so
    /// that it holds a whole number of them: the record is then exactly periodic in the stimulus,
    /// every harmonic lands on a bin centre and the analysis is exact rather than leaking across
    /// neighbours. It is the same argument the chirp measurement rests on — its period <i>is</i>
    /// the analysis FFT size — and the reference simulation is already set up for it, since
    /// <c>.tran 0 100m 5m</c> saves 95 ms, exactly 95 periods of its 1 kHz source.</para>
    ///
    /// <para>Falls back to <see cref="BaseAnalysisRateHz"/> when there is no single tone to lock
    /// to, which is the honest answer for a noise or multi-tone stimulus.</para>
    /// </summary>
    public double PeriodicRateHz(double fundamentalHz)
    {
        if (!PeriodicRecord || !(fundamentalHz > 0.0) || UsableS <= 0.0)
            return BaseAnalysisRateHz;

        int periods = (int)Math.Floor(fundamentalHz * UsableS);
        if (periods < 1)
            return BaseAnalysisRateHz;

        return FftSize * fundamentalHz / periods;
    }

    /// <summary>
    /// Where the fundamental search starts, lowered under the analysed band — the way every sweep
    /// runner lowers it under the tone it is about to play (<c>ThdSweepRunner</c>,
    /// <c>SteppedCaptureSupport</c> and <c>ImdSweepRunner</c> all take
    /// <c>Math.Min(current, tone / 2)</c>). This import does not know the tone in advance, but the
    /// band the user asked for is a statement of the lowest frequency worth finding.
    ///
    /// <para>The 200 Hz default guards against mains hum at 50/100 Hz being picked as the
    /// fundamental; a simulation has none, and the import removes the operating point before
    /// storing, so it has no DC either. Measured on a <c>.step param freq 50 5050 100</c> run with
    /// 10 Hz bins: at the 200 Hz floor the 50 and 150 Hz steps locked onto noise at 250 and 450 Hz,
    /// reading <c>-86.07</c> and <c>-39.39 dBV</c> with SNR <c>-95.4</c> and <c>-48.7 dB</c>,
    /// against <c>9.36 dBV</c> at their own frequency for every step from 250 Hz up. The 50 Hz step
    /// then collided with the one that really was 250 Hz.</para>
    ///
    /// <para><c>Min</c>, never <c>Max</c>: a band starting above 200 Hz keeps the hum defence, and
    /// no existing import can start searching higher than it did.</para>
    /// </summary>
    public double FundamentalSearchFloorHz => Math.Min(200.0, MeasurementLoFreqHz);

    public SpectrumPipelineOptions ToPipelineOptions() => new()
    {
        FftSize = FftSize,
        Window = Window,
        // One record is its own average. A simulation is deterministic, so averaging several
        // records of it would return the same numbers and take longer.
        Averaging = AveragingMode.Off,
        AverageCount = 1,
        MeasurementLoFreqHz = MeasurementLoFreqHz,
        MeasurementHiFreqHz = MeasurementHiFreqHz,
        FundamentalMinSearchHz = FundamentalSearchFloorHz,
        // Unweighted, like every runner: a weighting changes what "noise" means, and a stored
        // result has to say which one produced it rather than inheriting the live screen's.
        Weighting = WeightingSpec.None,
    };

    /// <summary>Applies a re-analysis request, leaving anything it did not ask about alone.</summary>
    public SimulationAnalysis With(SimulationReanalyzeRequest request) => this with
    {
        StartTimeS = request.StartTimeS ?? StartTimeS,
        LevelOffsetDb = request.LevelOffsetDb ?? LevelOffsetDb,
        FftSize = request.FftSize ?? FftSize,
        Window = request.Window ?? Window,
        MeasurementLoFreqHz = request.MeasurementLoFreqHz ?? MeasurementLoFreqHz,
        MeasurementHiFreqHz = request.MeasurementHiFreqHz ?? MeasurementHiFreqHz,
        PeriodicRecord = request.PeriodicRecord ?? PeriodicRecord,
    };

    internal static SimulationAnalysis Describe(
        SimulationFileInfo info,
        SimulationImportRequest request,
        double startTimeS,
        double usableS,
        IReadOnlyList<string> traceNames,
        IReadOnlyList<string> traceUnits,
        IReadOnlyList<double>? removedDcV = null,
        SimulationSweep? sweep = null)
    {
        int fftSize = request.FftSize > 0
            ? request.FftSize
            : SuggestedFftSize(usableS, request.MeasurementHiFreqHz, info.NativeRateHz);

        fftSize = Math.Clamp(fftSize, MinFftSize, LargestFftSize(usableS, info.NativeRateHz));

        var source = new SimulationSource(
            Path: info.Path,
            Title: info.Title,
            Date: info.Date,
            Plotname: info.Plotname,
            Flags: info.Flags,
            LeftTrace: traceNames[0],
            LeftUnit: traceUnits[0],
            RightTrace: traceNames.Count > 1 ? traceNames[1] : null,
            RightUnit: traceUnits.Count > 1 ? traceUnits[1] : null,
            NativeRateHz: info.NativeRateHz,
            PointCount: info.PointCount,
            SourceStartS: info.StartTimeS,
            SourceEndS: info.EndTimeS,
            Warnings: info.Warnings,
            SweepCount: info.Sweeps.Count,
            SweepIndex: info.Sweeps.Count > 1 ? sweep?.Index : null,
            SweepLabel: sweep?.Label);

        return new SimulationAnalysis(
            source, startTimeS, request.LevelOffsetDb, fftSize, request.Window,
            request.MeasurementLoFreqHz, request.MeasurementHiFreqHz, request.PeriodicRecord,
            removedDcV,
            // The samples about to be written begin exactly here, and that stays true however
            // often the result is re-analysed afterwards.
            PcmStartS: startTimeS);
    }

    /// <summary>
    /// Serialised as an <see cref="FftSpectrumParameters"/> carrying a
    /// <see cref="FftSpectrumParameters.Simulation"/> block, so a stored simulation's
    /// <c>params_json</c> is the same document shape every other FFT result's is and every existing
    /// reader of it — the report, the client's settings panel — keeps working untouched.
    /// </summary>
    public string ToParamsJson(JsonSerializerOptions options, double analysisRateHz) =>
        JsonSerializer.Serialize(ToParameters(analysisRateHz), options);

    public FftSpectrumParameters ToParameters(double analysisRateHz) => new(
        FftSize: FftSize,
        Window: Window,
        Averaging: AveragingMode.Off,
        AverageCount: 1,
        MeasurementLoFreqHz: MeasurementLoFreqHz,
        MeasurementHiFreqHz: MeasurementHiFreqHz,
        NotchOctaves: SpectrumMetrics.DefaultNotchOctaves,
        HarmonicCount: 12,
        FundamentalMinSearchHz: FundamentalSearchFloorHz,
        OtherPeaksCount: 20,
        ProcessLeft: true,
        ProcessRight: Source.RightTrace is not null,
        SampleRateHz: analysisRateHz,
        ChannelCount: Source.RightTrace is null ? 1 : 2,
        BlockFrameCount: 0,
        // A simulation has no analyzer front end and no generator. Zeros here would read as "the
        // 0 dBV range" and a silent generator is the truthful description of the second.
        InputRangeDbv: 0.0,
        OutputRangeDbv: 0.0,
        AutoRangeEnabled: null,
        Generator: new GeneratorConfig(
            new ChannelGeneratorConfig(false, []), new ChannelGeneratorConfig(false, [])))
    {
        Simulation = new SimulationParameters(
            Source, StartTimeS, LevelOffsetDb, PeriodicRecord, RemovedDcV, SamplesStartS),
    };

    /// <summary>
    /// Reads the simulation block back out of a stored result's <c>params_json</c>, or null when
    /// there is none — i.e. when the result is an ordinary measured capture.
    /// </summary>
    public static SimulationAnalysis? FromParamsJson(string paramsJson, JsonSerializerOptions options)
    {
        var parameters = JsonSerializer.Deserialize<FftSpectrumParameters>(paramsJson, options);
        if (parameters?.Simulation is not { } simulation)
            return null;

        return new SimulationAnalysis(
            simulation.Source,
            simulation.StartTimeS,
            simulation.LevelOffsetDb,
            parameters.FftSize,
            parameters.Window,
            parameters.MeasurementLoFreqHz,
            parameters.MeasurementHiFreqHz,
            simulation.PeriodicRecord,
            simulation.RemovedDcV,
            // Null on a row written before this was recorded, where the samples do begin at the
            // stored start time -- see SimulationParameters.PcmStartS.
            simulation.PcmStartS ?? simulation.StartTimeS);
    }
}
