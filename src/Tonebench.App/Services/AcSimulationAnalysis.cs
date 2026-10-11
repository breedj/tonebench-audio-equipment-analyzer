using System.Text.Json;
using Tonebench.Data;
using Tonebench.Dsp;

namespace Tonebench.App.Services;

/// <summary>
/// Statistics of a simulator's frequency axis — the counterpart of <see cref="TimeAxis"/>, and what
/// an AC import needs in order to say whether a sweep can be measured from.
///
/// <para>Named <c>AcFrequencyAxis</c> rather than <c>FrequencyAxis</c> because
/// <see cref="BandwidthAnalyzer"/> already has a private nested type of that name. The two would
/// not collide to the compiler and would collide badly to a reader.</para>
/// </summary>
public sealed record AcFrequencyAxis(
    double StartHz,
    double EndHz,
    int PointCount,
    bool IsMonotonic,
    int FirstNonMonotonicIndex,
    double PointsPerDecade,
    bool IsLogSpaced)
{
    /// <summary>
    /// How far the typical step may be from the average step, in log terms, before the sweep is
    /// called linear. 10 % separates the two cases by a mile — see <see cref="IsRatioConstant"/>.
    /// </summary>
    private const double LogSpacingTolerance = 0.10;

    public static AcFrequencyAxis Of(ReadOnlySpan<double> freqHz)
    {
        if (freqHz.Length < 2)
            throw new ArgumentException("A frequency axis needs at least two points.", nameof(freqHz));

        bool monotonic = true;
        int firstBad = -1;
        for (int i = 1; i < freqHz.Length; i++)
        {
            if (freqHz[i] <= freqHz[i - 1])
            {
                monotonic = false;
                firstBad = i;
                break;
            }
        }

        double startHz = freqHz[0];
        double endHz = freqHz[^1];
        double decades = startHz > 0.0 && endHz > startHz ? Math.Log10(endHz / startHz) : 0.0;
        double perDecade = decades > 0.0 ? (freqHz.Length - 1) / decades : 0.0;

        return new AcFrequencyAxis(
            StartHz: startHz,
            EndHz: endHz,
            PointCount: freqHz.Length,
            IsMonotonic: monotonic,
            FirstNonMonotonicIndex: firstBad,
            PointsPerDecade: perDecade,
            IsLogSpaced: monotonic && IsRatioConstant(freqHz));
    }

    /// <summary>
    /// Whether the steps are a constant <i>ratio</i> rather than a constant difference, which is
    /// what tells a <c>dec</c>/<c>oct</c> sweep from a <c>lin</c> one. Read from the data rather
    /// than from LTspice's <c>log</c> flag or QSpice's <c>Abscissa: ... dec</c> line, so one rule
    /// serves both simulators and anything else that writes this container.
    ///
    /// <para>The test is the <b>median</b> step against the <b>average</b> one, both in log terms.
    /// On a log sweep every step is the same, so the two agree exactly. On a linear sweep the
    /// ratios start large and fall towards 1, so the median sits far below the average: a
    /// 10 Hz–20 kHz linear sweep's median step is 0.0100 decades against an average of 0.0382, a
    /// factor of nearly four.</para>
    ///
    /// <para><b>Not "every ratio matches", which was tried and is wrong on real files.</b> Both
    /// reference sweeps are honest <c>.ac dec 100</c> runs whose median ratio is
    /// 10<sup>0.01</sup> to the last digit, and both carry one irregular step at an end — LTspice's
    /// strays by 10 % of the step and QSpice's by 92 %. Requiring every step to match called both
    /// of them linear and put a wrong warning on the one case this is for.</para>
    /// </summary>
    private static bool IsRatioConstant(ReadOnlySpan<double> freqHz)
    {
        if (freqHz.Length < 3 || freqHz[0] <= 0.0 || freqHz[^1] <= freqHz[0])
            return false;

        var steps = new double[freqHz.Length - 1];
        for (int i = 1; i < freqHz.Length; i++)
            steps[i - 1] = Math.Log10(freqHz[i] / freqHz[i - 1]);

        Array.Sort(steps);
        double median = steps[steps.Length / 2];
        double average = Math.Log10(freqHz[^1] / freqHz[0]) / (freqHz.Length - 1);
        if (median <= 0.0 || average <= 0.0)
            return false;

        return Math.Abs(median - average) <= LogSpacingTolerance * average;
    }
}

/// <summary>
/// Everything about how an imported <c>.ac</c> sweep is analysed, and how that travels in
/// <c>params_json</c> — the frequency-domain counterpart of <see cref="SimulationAnalysis"/>, with
/// the same three members (<c>ToParamsJson</c>, <c>FromParamsJson</c>, <c>With</c>) so the two read
/// alike.
///
/// <para><b>What is re-appliable, and what is not.</b> The band, the bandwidth reference, the level
/// offset and — when both curves were imported — which of them is the reference, all change without
/// the file. What cannot change is <i>which traces were imported</i>: the other columns were
/// discarded, exactly as on the transient side. And there is no smoothing setting at all: a
/// noiseless analytic curve has nothing to smooth, and not smoothing is what keeps the stored curve
/// losslessly re-analysable.</para>
/// </summary>
public sealed record AcSimulationAnalysis(
    AcSimulationSource Source,
    double LevelOffsetDb,
    double AnalysisLowHz,
    double AnalysisHighHz,
    BandwidthReferenceSpec Reference,
    bool RightChannelIsReference)
{
    /// <summary>
    /// The most points a sweep may carry. A <c>.ac dec 100</c> run over the audio band is a few
    /// hundred; this leaves room for a very dense one while refusing an <c>.ac lin 10meg</c>, whose
    /// rows would be tens of megabytes of f32 blob for a curve nobody can read.
    /// </summary>
    public const int MaxPoints = 65_536;

    /// <summary>Applies a re-analysis request, leaving anything it did not ask about alone.</summary>
    public AcSimulationAnalysis With(AcSimulationReanalyzeRequest request) => this with
    {
        LevelOffsetDb = request.LevelOffsetDb ?? LevelOffsetDb,
        AnalysisLowHz = request.AnalysisLowHz ?? AnalysisLowHz,
        AnalysisHighHz = request.AnalysisHighHz ?? AnalysisHighHz,
        Reference = request.Reference ?? Reference,
        RightChannelIsReference = request.RightChannelIsReference ?? RightChannelIsReference,
    };

    /// <summary>
    /// Serialised as a <see cref="FrequencyResponseParameters"/> carrying an
    /// <see cref="FrequencyResponseParameters.AcSimulation"/> block, so a stored simulation's
    /// <c>params_json</c> is the same document shape every other response result's is and every
    /// existing reader of it keeps working untouched.
    /// </summary>
    public string ToParamsJson(JsonSerializerOptions options) =>
        JsonSerializer.Serialize(ToParameters(), options);

    public FrequencyResponseParameters ToParameters() => new(
        // An AC run is a sweep of its own choosing, not a chirp over a span at a resolution, so
        // these two carry the sweep's end and a multiplier of one rather than a plausible-looking
        // figure. The AcSimulation block below is where its real shape is recorded.
        FrequencySpanHz: Source.EndFreqHz,
        ResolutionMultiplier: 1,
        // An AC analysis is *linear*: every drive level gives the same transfer function, and the
        // file does not record what the source was set to anyway. A number here would read as a
        // level that was chosen, which is the "zeros would read as the 0 dBV range" mistake.
        LevelMode: "NotApplicable",
        LevelValue: 0.0,
        LevelUnit: "",
        RightChannelIsReference: RightChannelIsReference,
        // None, and deliberately not offered: see the type's own remarks.
        SmoothingDenominator: 0,
        AverageCount: 1,
        GeneratorAmpDbv: null,
        BandwidthReference: Reference.Mode.ToString(),
        BandwidthReferenceLowHz: Reference.LowHz,
        BandwidthReferenceHighHz: Reference.HighHz)
    {
        AcSimulation = new AcSimulationParameters(Source, LevelOffsetDb, AnalysisLowHz, AnalysisHighHz),
    };

    /// <summary>
    /// Reads the AC block back out of a stored result's <c>params_json</c>, or null when there is
    /// none — i.e. when the result is a measured sweep. This is also how the server tells an
    /// imported AC result from an imported transient one.
    /// </summary>
    public static AcSimulationAnalysis? FromParamsJson(string paramsJson, JsonSerializerOptions options)
    {
        var parameters = JsonSerializer.Deserialize<FrequencyResponseParameters>(paramsJson, options);
        if (parameters?.AcSimulation is not { } simulation)
            return null;

        var mode = Enum.TryParse(parameters.BandwidthReference, out BandwidthReferenceMode parsed)
            ? parsed
            : BandwidthReferenceMode.BandMedian;

        return new AcSimulationAnalysis(
            simulation.Source,
            simulation.LevelOffsetDb,
            simulation.AnalysisLowHz,
            simulation.AnalysisHighHz,
            new BandwidthReferenceSpec(
                mode,
                parameters.BandwidthReferenceLowHz ?? BandwidthReferenceSpec.DefaultLowHz,
                parameters.BandwidthReferenceHighHz ?? BandwidthReferenceSpec.DefaultHighHz),
            parameters.RightChannelIsReference);
    }
}

/// <summary>What a re-analysis may change. Everything is optional; null leaves that setting alone.</summary>
public sealed record AcSimulationReanalyzeRequest(
    double? LevelOffsetDb = null,
    double? AnalysisLowHz = null,
    double? AnalysisHighHz = null,
    BandwidthReferenceSpec? Reference = null,
    bool? RightChannelIsReference = null);
