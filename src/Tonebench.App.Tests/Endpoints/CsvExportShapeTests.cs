using System.Reflection;
using Tonebench.App.Endpoints;
using Tonebench.App.Services;

// Both are nested in the endpoint class, where they belong — aliased so the expectation table and
// the InlineData rows below stay readable.
using CsvExportShape = Tonebench.App.Endpoints.MeasurementsEndpoints.CsvExportShape;
using ImpulseCsvPart = Tonebench.App.Endpoints.MeasurementsEndpoints.ImpulseCsvPart;

namespace Tonebench.App.Tests.Endpoints;

/// <summary>
/// Which exporter each stored kind's CSV goes through, and the three parts an impulse response has.
///
/// <para><b>Why a coverage test and not a few examples.</b> <c>MeasurementsEndpoints.ExportShapeFor</c>
/// <i>falls back</i> to the spectrum renderer, so a kind nobody gave an arm does not fail loudly —
/// it reads a result's <c>spectrum</c> rows, finds none, and answers 404 to a CSV button the history
/// panel renders on every row. That is exactly what <c>ImpulseResponse</c> did from the day it was
/// added until it was noticed. So the expectations below are keyed off the kind constants
/// themselves, read by reflection: a new kind cannot be added to <see cref="MeasurementSaveService"/>
/// without this test failing until someone has said, in one line, which of its tables the export
/// reads.</para>
/// </summary>
public class CsvExportShapeTests
{
    /// <summary>
    /// Every kind's exporter. The FFT kind is not a constant anywhere server-side — it is the
    /// fallback and never compared — so it is the one entry named as a literal.
    /// </summary>
    private static readonly Dictionary<string, CsvExportShape> Expected = new(StringComparer.Ordinal)
    {
        ["FFTSpectrum"] = CsvExportShape.Spectrum,
        [MeasurementSaveService.ThdVsFrequencyKind] = CsvExportShape.Sweep,
        [MeasurementSaveService.ThdVsAmplitudeKind] = CsvExportShape.Sweep,
        [MeasurementSaveService.ImdVsAmplitudeKind] = CsvExportShape.Sweep,
        [MeasurementSaveService.FrequencyResponseKind] = CsvExportShape.Response,
        [MeasurementSaveService.SteppedResponseKind] = CsvExportShape.Response,
        [MeasurementSaveService.CrosstalkKind] = CsvExportShape.Response,
        [MeasurementSaveService.ImpedanceKind] = CsvExportShape.Impedance,
        [MeasurementSaveService.OutputImpedanceKind] = CsvExportShape.Impedance,
        [MeasurementSaveService.ImpulseResponseKind] = CsvExportShape.Impulse,
    };

    /// <summary>Every <c>public const string *Kind</c> on the save service — the kinds a result can actually be saved under.</summary>
    private static IEnumerable<string> SavedKinds() =>
        typeof(MeasurementSaveService)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Where(f => f.Name.EndsWith("Kind", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void EveryKindThatCanBeSaved_HasAnExporterSomeoneChose()
    {
        var unaccounted = SavedKinds().Where(kind => !Expected.ContainsKey(kind)).ToArray();

        Assert.True(
            unaccounted.Length == 0,
            $"These kinds have no expected CSV exporter, so nobody has said which of their tables an "
                + $"export reads — and ExportShapeFor will quietly send them to the spectrum renderer, "
                + $"which answers 404: {string.Join(", ", unaccounted)}");
    }

    [Fact]
    public void EachKind_ExportsThroughTheRendererItsTablesMatch()
    {
        foreach (var (kind, shape) in Expected)
            Assert.Equal(shape, MeasurementsEndpoints.ExportShapeFor(kind));
    }

    /// <summary>
    /// The regression, stated as such: an impulse response has to reach its own renderer rather than
    /// the spectrum fallback, whose rows it does not have.
    /// </summary>
    [Fact]
    public void AnImpulseResponse_DoesNotFallThroughToTheSpectrumRenderer()
    {
        Assert.Equal(
            CsvExportShape.Impulse,
            MeasurementsEndpoints.ExportShapeFor(MeasurementSaveService.ImpulseResponseKind));
    }

    /// <summary>
    /// The plain link the history panel renders carries no <c>part</c> at all, so an absent one has
    /// to be the default rather than an error — and the default is the samples, the one thing an
    /// impulse result stores at full resolution and the only one of the three that cannot be
    /// re-derived from the others.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("impulse")]
    [InlineData("IMPULSE")]
    public void AnAbsentOrDefaultPart_IsTheSamples(string? part)
        => Assert.Equal(ImpulseCsvPart.Impulse, MeasurementsEndpoints.ImpulsePart(part));

    [Fact]
    public void TheOtherTwoGrids_AreNamedParts()
    {
        // Not a Theory: the parameter type is internal, which an xUnit test method may not expose.
        Assert.Equal(ImpulseCsvPart.Curves, MeasurementsEndpoints.ImpulsePart("curves"));
        Assert.Equal(ImpulseCsvPart.Curves, MeasurementsEndpoints.ImpulsePart("Curves"));
        Assert.Equal(ImpulseCsvPart.Harmonics, MeasurementsEndpoints.ImpulsePart("harmonics"));
    }

    /// <summary>
    /// A name that is not one of the three is refused rather than silently treated as the default:
    /// a caller who asked for something specific and got the samples has no way to tell.
    /// </summary>
    [Theory]
    [InlineData("response")]
    [InlineData("magnitude")]
    [InlineData("ir")]
    public void AnUnknownPart_IsRefused(string part)
        => Assert.Null(MeasurementsEndpoints.ImpulsePart(part));
}
