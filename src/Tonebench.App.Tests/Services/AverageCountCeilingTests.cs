using System.Reflection;
using Tonebench.App.Services;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// That every kind which folds whole records into one estimate stops at the same count.
///
/// <para>Six kinds declare a <c>MaxAverageCount</c> and they were not all the same: the impedance
/// screen was raised to 20 on 2026-10-20 while the rest stayed at 10, and the rest followed on
/// 2026-10-21. A reflection test rather than six assertions because the failure mode is a
/// <i>seventh</i> kind, or a copied constant left behind — neither of which a hand-written list
/// notices.</para>
///
/// <para>The sweeps are deliberately not here. THD and IMD cap their averaging at 64, and that is a
/// different quantity: they average spectra at one point of a grid, where these average whole
/// records of one measurement.</para>
/// </summary>
public class AverageCountCeilingTests
{
    private static readonly (string Name, FieldInfo Field)[] Ceilings =
        typeof(FrequencyResponseRequest).Assembly
            .GetTypes()
            .Where(t => t.Namespace == typeof(FrequencyResponseRequest).Namespace)
            .Select(t => (t.Name, Field: t.GetField("MaxAverageCount", BindingFlags.Public | BindingFlags.Static)))
            .Where(entry => entry.Field is { IsLiteral: true })
            .Select(entry => (entry.Name, Field: entry.Field!))
            .ToArray();

    [Fact]
    public void EveryKindThatAveragesWholeRecords_StopsAtTheSameCount()
    {
        Assert.True(Ceilings.Length >= 5, $"expected the kinds that average records, found {Ceilings.Length}");

        var byValue = Ceilings
            .GroupBy(entry => (int)entry.Field.GetRawConstantValue()!)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Name).ToArray());

        Assert.True(
            byValue.Count == 1,
            "the kinds disagree about how many records one measurement may fold in: "
                + string.Join("; ", byValue.Select(pair => $"{pair.Key} -> {string.Join(", ", pair.Value)}")));
    }

    [Fact]
    public void TheCeilingIsTwenty_AndTheClientKnowsTheSameNumber()
    {
        // web/src/measurements/averaging.ts carries this number for the panels' own inputs and
        // validation, and averaging.test.ts asserts it there. A panel that stops short of the
        // server's ceiling refuses a count the server would have taken, silently.
        Assert.Equal(20, FrequencyResponseRequest.MaxAverageCount);
        Assert.Equal(1, FrequencyResponseRequest.MinAverageCount);
    }

    [Fact]
    public void TheCeilingIsAccepted_AndOnePastItIsNot()
    {
        int at = FrequencyResponseRequest.MaxAverageCount;

        foreach (var validate in ValidatorsFor(at))
            validate();

        foreach (var validate in ValidatorsFor(at + 1))
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(validate);
            Assert.Contains($"between 1 and {at}", error.Message);
        }
    }

    /// <summary>One request per kind, at a given count, ready to validate.</summary>
    private static Action[] ValidatorsFor(int averages) =>
    [
        FrequencyResponse(averages).Validate,
        SteppedResponse(averages).Validate,
        Impedance(averages).Validate,
    ];

    private static FrequencyResponseRequest FrequencyResponse(int averages) => new(
        FrequencySpanHz: 24_000.0, ResolutionMultiplier: 1,
        ThdSweepLevelMode.GeneratorVoltage, LevelValue: -20.0, ThdSweepLevelUnit.Dbv, LoadOhms: 8.0,
        RightChannelIsReference: true, SmoothingDenominator: 24, AverageCount: averages, Continuous: false);

    private static SteppedResponseRequest SteppedResponse(int averages) => new(
        StartFreqHz: 20.0, EndFreqHz: 20_000.0, StepsPerOctave: 5,
        ThdSweepLevelMode.GeneratorVoltage, LevelValue: -20.0, ThdSweepLevelUnit.Dbv, LoadOhms: 8.0,
        GenerateLeft: true, GenerateRight: true,
        FftSize: 32_768, WindowType.Hann, AverageCount: averages, SampleRateHz: 96_000.0,
        AdaptiveResolution: false, RightChannelIsReference: true);

    private static ImpedanceRequest Impedance(int averages) => new(
        ImpedanceMethod.ReferenceResistor, ImpedanceStimulus.PinkNoise,
        ReferenceOhms: 100.0, RightChannelIsReference: false,
        FrequencySpanHz: 24_000.0, ResolutionMultiplier: 1,
        LevelValue: -20.0, LevelUnit: ThdSweepLevelUnit.Dbv,
        SmoothingDenominator: 12, AverageCount: averages);
}
