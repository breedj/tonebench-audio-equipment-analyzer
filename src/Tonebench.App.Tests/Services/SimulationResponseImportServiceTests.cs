using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tonebench.App.Services;
using Tonebench.Data;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// Importing a simulator's <c>.ac</c> run as a frequency response, end to end: a synthesized
/// <c>.raw</c> in, <c>response_trace</c> rows out.
///
/// <para>The anchor is <see cref="Import_OfAOnePoleLowPass_ReadsItsCornerWhereTheArithmeticSaysItIs"/>.
/// A one-pole is the only transfer function whose −3 dB corner is known exactly without measuring
/// anything, so it plays the part the "1 % H2 reads THD 1.000 %" case plays for the transient
/// import: one number that cannot be quietly wrong at any step between the bytes and the row.</para>
///
/// <para>Verified separately against Joost's two real sweeps, whose figures are recorded in the
/// commit rather than here — the files are 1.7 MB and live outside the repository. EL84: passband
/// median 18.1403 dB, 18.1405 dB and 179.95° at 1 kHz, ±1 dB 11.195 Hz–100.55 kHz, −3 dB
/// 6.045 Hz–201.6 kHz. QSpice AudioAmp: 32.085 dB raw against 32.160 dB divided by V(in).</para>
/// </summary>
public class SimulationResponseImportServiceTests : IDisposable
{
    private const double CornerHz = 1000.0;

    private readonly string _dir = Directory.CreateTempSubdirectory("tonebench-ac-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A one-pole low pass is −3.0103 dB and −45° at its corner, by arithmetic rather than by
    /// measurement. Referenced to the peak (its own DC gain) the −3 dB corner has to land on the
    /// pole, which pins the parser, the complex-to-dB conversion, the unwrapper, the log-frequency
    /// interpolation in <c>BandwidthAnalyzer</c> and the stored row in one number.
    /// </summary>
    [Theory]
    [InlineData(false)]  // LTspice: a complex abscissa
    [InlineData(true)]   // QSpice:  a bare f64 abscissa
    public async Task Import_OfAOnePoleLowPass_ReadsItsCornerWhereTheArithmeticSaysItIs(bool qspice)
    {
        double[] freqHz = LogSweep(1.0, 1e6, 100);
        string raw = WriteAcSweep($"pole-{qspice}.raw", freqHz, OnePole(freqHz), qspice: qspice);

        await WithHarnessAsync(async h =>
        {
            long resultId = await h.ImportAsync(raw, PeakReferenced());
            var gain = await h.GainAsync(resultId);

            // 997.63 Hz, not 1000: the -3 dB row means exactly 3.0 dB, where a one-pole is at
            // 1/sqrt(1 + x^2) = 10^(-0.15), i.e. x = 0.99763. The half-power point (-3.0103 dB) is
            // the one at 1000 Hz, and it is asserted separately below. Agreeing to four digits with
            // points 2.33 % apart is the log-frequency interpolation doing its job.
            double minusThreeDbHz = CornerHz * Math.Sqrt(Math.Pow(10.0, 0.3) - 1.0);
            Assert.Equal(minusThreeDbHz, gain.ThreeDb.UpperHz, minusThreeDbHz * 1e-4);
            // The peak is the lowest point swept, not DC: at 1 Hz a 1 kHz pole is already
            // 4.3 microdecibels down.
            Assert.Equal(0.0, gain.PeakDb, 1e-4);

            int atCorner = NearestTo(gain, CornerHz);
            Assert.Equal(-3.0103, gain.MagnitudeMaxDb[atCorner], 0.002);
            Assert.Equal(-45.0, gain.PhaseDeg[atCorner], 0.05);
        });
    }

    /// <summary>
    /// The whole sweep is stored, one row entry per simulated point: <c>BuildPointTrace</c> neither
    /// smooths nor decimates, which is what makes a re-analysis a lossless re-read rather than a
    /// re-render. Min and max are the same number because there is nothing between them.
    /// </summary>
    [Fact]
    public async Task Import_StoresTheSweepAtFullResolution()
    {
        double[] freqHz = LogSweep(10.0, 100_000.0, 50);
        string raw = WriteAcSweep("full.raw", freqHz, OnePole(freqHz));

        await WithHarnessAsync(async h =>
        {
            var gain = await h.GainAsync(await h.ImportAsync(raw, PeakReferenced()));

            Assert.Equal(freqHz.Length, gain.FreqHz.Length);
            for (int i = 0; i < freqHz.Length; i++)
            {
                Assert.Equal(freqHz[i], gain.FreqHz[i], freqHz[i] * 1e-5);
                Assert.Equal(gain.MagnitudeMinDb[i], gain.MagnitudeMaxDb[i]);
            }
        });
    }

    /// <summary>
    /// A simulation's phase is exact, so the delay curves derived from it are too. A null here
    /// draws as a gap on the chart and a dash in the panel, which reads as something broken.
    /// </summary>
    [Fact]
    public async Task Import_ComputesGroupAndPhaseDelay()
    {
        double[] freqHz = LogSweep(10.0, 100_000.0, 100);
        string raw = WriteAcSweep("delay.raw", freqHz, OnePole(freqHz));

        await WithHarnessAsync(async h =>
        {
            var gain = await h.GainAsync(await h.ImportAsync(raw, PeakReferenced()));

            Assert.NotNull(gain.GroupDelayS);
            Assert.NotNull(gain.PhaseDelayS);

            // A one-pole's group delay at DC is 1/(2*pi*fc) -- 159.2 us for a 1 kHz corner.
            Assert.Equal(1.0 / (2.0 * Math.PI * CornerHz), gain.GroupDelayS![0], 5e-6);
        });
    }

    /// <summary>
    /// Dividing by a reference trace is what makes the result the circuit's own gain rather than a
    /// claim about the simulator's source. Measured on the real QSpice circuit, whose source sags
    /// to 0.9914 at 1 kHz, it is worth 0.075 dB; here the reference is a flat half, so it is worth
    /// exactly 6.0206 dB and can be asserted rather than estimated.
    /// </summary>
    [Fact]
    public async Task Import_WithAReferenceTrace_DividesByIt_AndKeepsBothSingles()
    {
        double[] freqHz = LogSweep(10.0, 100_000.0, 50);
        var output = OnePole(freqHz);
        var reference = Enumerable.Repeat(new Complex(0.5, 0.0), freqHz.Length).ToArray();
        string raw = WriteAcSweep("divide.raw", freqHz, output, reference);

        await WithHarnessAsync(async h =>
        {
            long plain = await h.ImportAsync(raw, PeakReferenced(), name: "plain");
            long divided = await h.ImportAsync(raw, PeakReferenced(), referenceTrace: true, name: "divided");

            var plainRows = await h.RowsAsync(plain);
            var dividedRows = await h.RowsAsync(divided);

            // One curve without a reference, three with: the gain and both of the singles it came
            // from, so the division can be undone and redone later.
            Assert.Single(plainRows);
            Assert.Equal(ResponseTraceKind.Gain, plainRows[0].Kind);
            Assert.Equal(3, dividedRows.Count);
            Assert.Contains(dividedRows, r => r.Kind == ResponseTraceKind.Left);
            Assert.Contains(dividedRows, r => r.Kind == ResponseTraceKind.Right);

            var plainGain = plainRows.Single(r => r.Kind == ResponseTraceKind.Gain);
            var dividedGain = dividedRows.Single(r => r.Kind == ResponseTraceKind.Gain);
            for (int i = 0; i < plainGain.FreqHz.Length; i++)
                Assert.Equal(plainGain.MagnitudeMaxDb[i] + 6.0206, dividedGain.MagnitudeMaxDb[i], 0.002);
        });
    }

    /// <summary>
    /// The band is a <b>search window, not a slice</b>: narrowing it and widening it again has to
    /// return the corners it started with, because the whole curve is still there. A re-analysis
    /// that trimmed the arrays instead would make the narrowing permanent and could only invent
    /// what it had discarded.
    /// </summary>
    [Fact]
    public async Task Reanalyze_NarrowingThenWideningTheBand_ReturnsTheOriginalCorners()
    {
        double[] freqHz = LogSweep(1.0, 1e6, 100);
        string raw = WriteAcSweep("window.raw", freqHz, OnePole(freqHz));

        await WithHarnessAsync(async h =>
        {
            long resultId = await h.ImportAsync(raw, PeakReferenced());
            var before = (await h.GainAsync(resultId)).ThreeDb;

            Assert.True(await h.Service.ReanalyzeAsync(
                resultId, new AcSimulationReanalyzeRequest(AnalysisLowHz: 100.0, AnalysisHighHz: 500.0),
                CancellationToken.None));
            var narrowed = (await h.GainAsync(resultId)).ThreeDb;

            Assert.True(await h.Service.ReanalyzeAsync(
                resultId, new AcSimulationReanalyzeRequest(AnalysisLowHz: 1.0, AnalysisHighHz: 1e6),
                CancellationToken.None));
            var widened = (await h.GainAsync(resultId)).ThreeDb;

            // Narrowing moved the answer: the corner is outside the window, so it reads clamped.
            Assert.NotEqual(before.UpperHz, narrowed.UpperHz, 1.0);

            // Widening brought it back. Not bit-for-bit -- the rows store f32 -- but to a part in
            // a million, which is the round-trip of the magnitudes and nothing else.
            Assert.Equal(before.UpperHz, widened.UpperHz, before.UpperHz * 1e-5);
            Assert.Equal(before.LowerHz, widened.LowerHz, Math.Max(before.LowerHz * 1e-5, 1e-9));
        });
    }

    /// <summary>
    /// The samples behind the curve are the rows themselves, so a re-analysis must leave the sweep
    /// alone and rewrite only what was derived from it.
    /// </summary>
    [Fact]
    public async Task Reanalyze_LeavesTheStoredCurveAlone()
    {
        double[] freqHz = LogSweep(10.0, 100_000.0, 50);
        string raw = WriteAcSweep("untouched.raw", freqHz, OnePole(freqHz));

        await WithHarnessAsync(async h =>
        {
            long resultId = await h.ImportAsync(raw, PeakReferenced());
            var before = await h.GainAsync(resultId);

            await h.Service.ReanalyzeAsync(
                resultId, new AcSimulationReanalyzeRequest(AnalysisLowHz: 50.0, AnalysisHighHz: 5000.0),
                CancellationToken.None);
            var after = await h.GainAsync(resultId);

            Assert.Equal(before.FreqHz, after.FreqHz);
            Assert.Equal(before.MagnitudeMaxDb, after.MagnitudeMaxDb);
            Assert.Equal(before.PhaseDeg, after.PhaseDeg);
        });
    }

    /// <summary>
    /// The level offset is applied to the gain at analysis time and never baked into the curve,
    /// which is what keeps it a setting: setting it and clearing it again has to come back to where
    /// it started.
    /// </summary>
    [Fact]
    public async Task Reanalyze_TheLevelOffset_IsAppliedAndRemovable()
    {
        double[] freqHz = LogSweep(10.0, 100_000.0, 50);
        string raw = WriteAcSweep("offset.raw", freqHz, OnePole(freqHz));

        await WithHarnessAsync(async h =>
        {
            long resultId = await h.ImportAsync(raw, PeakReferenced());
            float[] before = (await h.GainAsync(resultId)).MagnitudeMaxDb;

            await h.Service.ReanalyzeAsync(
                resultId, new AcSimulationReanalyzeRequest(LevelOffsetDb: -20.0), CancellationToken.None);
            float[] shifted = (await h.GainAsync(resultId)).MagnitudeMaxDb;

            await h.Service.ReanalyzeAsync(
                resultId, new AcSimulationReanalyzeRequest(LevelOffsetDb: 0.0), CancellationToken.None);
            float[] restored = (await h.GainAsync(resultId)).MagnitudeMaxDb;

            for (int i = 0; i < before.Length; i++)
            {
                Assert.Equal(before[i] - 20.0, shifted[i], 0.002);
                Assert.Equal(before[i], restored[i], 0.002);
            }
        });
    }

    /// <summary>
    /// Which of two curves is the reference can only be changed where both were imported. On a
    /// result holding one curve there is nothing to divide by, and saying so is better than
    /// silently ignoring the setting.
    /// </summary>
    [Fact]
    public async Task Reanalyze_SwappingTheReference_NeedsBothCurves()
    {
        double[] freqHz = LogSweep(10.0, 100_000.0, 50);
        var output = OnePole(freqHz);
        var reference = Enumerable.Repeat(new Complex(0.5, 0.0), freqHz.Length).ToArray();

        string one = WriteAcSweep("swap-one.raw", freqHz, output);
        string both = WriteAcSweep("swap-both.raw", freqHz, output, reference);

        await WithHarnessAsync(async h =>
        {
            long single = await h.ImportAsync(one, PeakReferenced(), name: "single");
            // Asking for the value it already has is a no-op; asking it to change is what there
            // is nothing to do with.
            Assert.True(await h.Service.ReanalyzeAsync(
                single, new AcSimulationReanalyzeRequest(RightChannelIsReference: false), CancellationToken.None));

            var refused = await Assert.ThrowsAsync<ArgumentException>(() => h.Service.ReanalyzeAsync(
                single, new AcSimulationReanalyzeRequest(RightChannelIsReference: true), CancellationToken.None));
            Assert.Contains("only one curve", refused.Message, StringComparison.OrdinalIgnoreCase);

            long pair = await h.ImportAsync(both, PeakReferenced(), referenceTrace: true, name: "pair");
            float[] forward = (await h.GainAsync(pair)).MagnitudeMaxDb;

            Assert.True(await h.Service.ReanalyzeAsync(
                pair, new AcSimulationReanalyzeRequest(RightChannelIsReference: false), CancellationToken.None));
            float[] swapped = (await h.GainAsync(pair)).MagnitudeMaxDb;

            // Dividing the other way round is the reciprocal, i.e. the same curve negated.
            for (int i = 0; i < forward.Length; i++)
                Assert.Equal(-forward[i], swapped[i], 0.002);
        });
    }

    /// <summary>
    /// The gain's phase is defined as the difference of two unwrapped curves, normalised once, and
    /// the import and the re-analysis have to use that one definition. They differ otherwise by a
    /// whole number of turns -- invisible in group delay, which differentiates a constant away, and
    /// a real error in phase delay, which divides the absolute phase by frequency.
    /// </summary>
    [Fact]
    public async Task Reanalyze_RebuildsTheSameGainPhaseAsTheImport()
    {
        double[] freqHz = LogSweep(1.0, 1e6, 100);
        var output = OnePole(freqHz);
        var reference = Enumerable.Repeat(new Complex(0.5, 0.0), freqHz.Length).ToArray();
        string raw = WriteAcSweep("phase.raw", freqHz, output, reference);

        await WithHarnessAsync(async h =>
        {
            long resultId = await h.ImportAsync(raw, PeakReferenced(), referenceTrace: true);
            var imported = await h.GainAsync(resultId);

            await h.Service.ReanalyzeAsync(
                resultId, new AcSimulationReanalyzeRequest(AnalysisLowHz: 20.0), CancellationToken.None);
            var rebuilt = await h.GainAsync(resultId);

            for (int i = 0; i < imported.PhaseDeg.Length; i++)
                Assert.Equal(imported.PhaseDeg[i], rebuilt.PhaseDeg[i], 0.002);
            for (int i = 0; i < imported.PhaseDelayS!.Length; i++)
                Assert.Equal(imported.PhaseDelayS[i], rebuilt.PhaseDelayS![i], 1e-9);
        });
    }

    [Fact]
    public async Task Reanalyze_OfAMeasuredResult_IsRefusedRatherThanGuessed()
    {
        await WithHarnessAsync(async h =>
            Assert.False(await h.Service.ReanalyzeAsync(
                4242, new AcSimulationReanalyzeRequest(), CancellationToken.None)));
    }

    /// <summary>
    /// The row is marked as imported, which is the one thing that puts it in the History list's
    /// Simulations group -- that grouping keys on <c>source</c> alone and knows nothing of kinds.
    /// And <c>capture.fs</c> has to read back as an integer, or the Dapper trap
    /// <c>ResultSummaryRowRaw</c> documents takes down the whole listing rather than this one row.
    /// </summary>
    [Fact]
    public async Task Import_IsMarkedAsASimulation_AndTheListingStillReads()
    {
        double[] freqHz = LogSweep(10.0, 100_000.0, 50);
        string raw = WriteAcSweep("listed.raw", freqHz, OnePole(freqHz));

        await WithHarnessAsync(async h =>
        {
            long resultId = await h.ImportAsync(raw, PeakReferenced());

            var detail = (await h.Measurements.GetResultAsync(resultId, CancellationToken.None))!;
            Assert.Equal(MeasurementSource.Simulation, detail.Source);

            var listed = await h.Measurements.ListResultsAsync(
                new MeasurementFilter(MeasurementTypeId: h.TypeId), CancellationToken.None);
            Assert.Equal(MeasurementSource.Simulation, Assert.Single(listed).Source);
        });
    }

    [Fact]
    public async Task Inspect_ListsEveryTraceWithItsUnit_AndDescribesTheSweep()
    {
        double[] freqHz = LogSweep(10.0, 100_000.0, 100);
        string raw = WriteAcSweep("inspect.raw", freqHz, OnePole(freqHz));

        WithService(service =>
        {
            var info = service.Inspect(raw);

            Assert.Equal(freqHz.Length, info.PointCount);
            Assert.Equal(10.0, info.StartFreqHz, 1e-9);
            Assert.Equal(100_000.0, info.EndFreqHz, 1.0);
            Assert.Equal(100.0, info.PointsPerDecade, 0.5);
            Assert.True(info.IsLogSpaced);
            Assert.Equal("V(out)", Assert.Single(info.Traces).Name);
            Assert.Equal("V", info.Traces[0].Unit);
        });
    }

    [Fact]
    public void Inspect_OfALinearSweep_SaysItWillDrawBadly()
    {
        double[] freqHz = [.. Enumerable.Range(0, 200).Select(i => 10.0 + (i * 100.0))];
        string raw = WriteAcSweep("linear.raw", freqHz, OnePole(freqHz));

        WithService(service =>
        {
            var info = service.Inspect(raw);

            Assert.False(info.IsLogSpaced);
            Assert.Contains(info.Warnings, w => w.Contains(".ac dec", StringComparison.OrdinalIgnoreCase));
        });
    }

    /// <summary>
    /// A real `.ac dec` sweep is not perfectly regular: both reference files carry one irregular
    /// step at an end -- LTspice's strays by 10 % of a step and QSpice's by 92 %. An earlier
    /// detector asked that *every* ratio match and called both of those honest log sweeps linear,
    /// putting the warning on exactly the case it exists to approve. The test is the typical step
    /// against the average one, which a stray step at an end cannot move.
    /// </summary>
    [Theory]
    [InlineData(0.10)]
    [InlineData(0.92)]
    public void Inspect_OfALogSweepWithOneIrregularStep_StillCallsItLogSpaced(double strayFraction)
    {
        double[] freqHz = LogSweep(0.1, 1e6, 100);

        // Pull the second point towards the first by a fraction of one step, the way both
        // simulators' own grids do at one end.
        double step = Math.Log10(freqHz[1] / freqHz[0]);
        freqHz[1] = freqHz[0] * Math.Pow(10.0, step * (1.0 - strayFraction));

        string raw = WriteAcSweep($"stray-{strayFraction}.raw", freqHz, OnePole(freqHz));

        WithService(service =>
        {
            var info = service.Inspect(raw);

            Assert.True(info.IsLogSpaced);
            Assert.DoesNotContain(info.Warnings, w => w.Contains(".ac dec", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void Inspect_OfASparseSweep_SaysTheCornerCannotBeLocatedClosely()
    {
        double[] freqHz = LogSweep(10.0, 100_000.0, 5);
        string raw = WriteAcSweep("sparse.raw", freqHz, OnePole(freqHz));

        WithService(service =>
            Assert.Contains(
                service.Inspect(raw).Warnings,
                w => w.Contains("points per decade", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// A frequency axis that restarts in a file that does <b>not</b> claim to be stepped is still
    /// refused, and that is the whole job of the header flag. Both simulators write <c>stepped</c>
    /// on a <c>.step</c>ped run (measured), so a backwards axis without it is a damaged or
    /// rewritten file -- and the arithmetic would otherwise turn it into several
    /// plausible-looking results rather than saying anything.
    /// </summary>
    [Fact]
    public void Inspect_OfAnAxisThatRestartsWithoutTheSteppedFlag_IsRefused()
    {
        double[] freqHz = [.. LogSweep(10.0, 1000.0, 20), .. LogSweep(10.0, 1000.0, 20)];
        string raw = WriteAcSweep("not-stepped.raw", freqHz, OnePole(freqHz));

        WithService(service =>
        {
            var ex = Assert.Throws<FormatException>(() => service.Inspect(raw));
            Assert.Contains(".step", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("stepped` flag", ex.Message, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// The mistake is almost always the wrong screen rather than the wrong file, so the refusal
    /// names the screen the file does belong on.
    /// </summary>
    [Fact]
    public void Inspect_OfATransientRun_PointsAtTheFftScreen()
    {
        double[] freqHz = LogSweep(10.0, 1000.0, 20);
        string raw = WriteAcSweep("tran.raw", freqHz, OnePole(freqHz), plotname: "Transient Analysis");

        WithService(service =>
        {
            var ex = Assert.Throws<FormatException>(() => service.Inspect(raw));
            Assert.Contains("FFT analysis measurement", ex.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// Waveform compression, maximum timestep and <c>1 / duration</c> resolution are transient
    /// concepts. An AC file carries <c>nocompression</c> in its flags incidentally, and reading it
    /// would be interpreting a time-domain field on a frequency sweep.
    /// </summary>
    [Fact]
    public void Inspect_OfAnAcSweep_SaysNothingAboutTransientSettings()
    {
        double[] freqHz = LogSweep(10.0, 100_000.0, 100);
        string raw = WriteAcSweep("quiet.raw", freqHz, OnePole(freqHz), flags: "complex double forward log");

        WithService(service =>
        {
            foreach (string warning in service.Inspect(raw).Warnings)
            {
                Assert.DoesNotContain("plotwinsize", warning, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("compression", warning, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("timestep", warning, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("numdgt", warning, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("bins", warning, StringComparison.OrdinalIgnoreCase);
            }
        });
    }

    [Fact]
    public async Task Import_WithNoReference_SaysTheLevelsAreRelativeToOneVolt()
    {
        double[] freqHz = LogSweep(10.0, 100_000.0, 50);
        string raw = WriteAcSweep("absolute.raw", freqHz, OnePole(freqHz));

        await WithHarnessAsync(async h =>
        {
            long resultId = await h.ImportAsync(raw, PeakReferenced());
            var stored = h.ReadAnalysis(
                (await h.Measurements.GetResultAsync(resultId, CancellationToken.None))!.ParamsJson);

            Assert.Null(stored.Source.ReferenceTrace);
            Assert.Contains(stored.Source.Warnings, w => w.Contains("AC 1", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Import_WhenTheReferenceSags_SaysDividingIsWhatFixesIt()
    {
        double[] freqHz = LogSweep(10.0, 100_000.0, 50);
        var output = OnePole(freqHz);
        var sagging = freqHz.Select((_, i) => new Complex(1.0 - (i * 0.002), 0.0)).ToArray();
        string raw = WriteAcSweep("sag.raw", freqHz, output, sagging);

        await WithHarnessAsync(async h =>
        {
            long resultId = await h.ImportAsync(raw, PeakReferenced(), referenceTrace: true);
            var stored = h.ReadAnalysis(
                (await h.Measurements.GetResultAsync(resultId, CancellationToken.None))!.ParamsJson);

            Assert.Contains(stored.Source.Warnings, w => w.Contains("loaded by the", StringComparison.Ordinal));
        });
    }

    /// <summary>Both abscissa layouts have to read identically, or one simulator is silently wrong.</summary>
    [Fact]
    public async Task Import_OfAQSpiceSweep_ReadsItExactlyAsAnLtspiceOne()
    {
        double[] freqHz = LogSweep(10.0, 100_000.0, 50);
        var output = OnePole(freqHz);
        string lt = WriteAcSweep("dialect-lt.raw", freqHz, output);
        string qs = WriteAcSweep("dialect-qs.qraw", freqHz, output, qspice: true);

        await WithHarnessAsync(async h =>
        {
            var a = await h.GainAsync(await h.ImportAsync(lt, PeakReferenced(), name: "lt"));
            var b = await h.GainAsync(await h.ImportAsync(qs, PeakReferenced(), name: "qs"));

            Assert.Equal(a.MagnitudeMaxDb, b.MagnitudeMaxDb);
            Assert.Equal(a.PhaseDeg, b.PhaseDeg);
            Assert.Equal(a.ThreeDb.UpperHz, b.ThreeDb.UpperHz, 1e-9);
        });
    }

    [Fact]
    public async Task Import_OfAnInvertingCircuit_KeepsThePhaseAtHalfATurn()
    {
        double[] freqHz = LogSweep(10.0, 100_000.0, 50);
        var inverted = OnePole(freqHz).Select(c => -c).ToArray();
        string raw = WriteAcSweep("inverting.raw", freqHz, inverted);

        await WithHarnessAsync(async h =>
        {
            var gain = await h.GainAsync(await h.ImportAsync(raw, PeakReferenced()));

            // Stored unwrapped, so compare the turn it sits on rather than the number. Half a
            // turn less the pole's own 0.57 deg at 10 Hz -- the inversion is what this is about,
            // but the expectation still has to be the circuit's actual phase.
            double expected = 180.0 - (Math.Atan2(10.0, CornerHz) * 180.0 / Math.PI);
            double wrapped = ((gain.PhaseDeg[0] % 360.0) + 360.0) % 360.0;
            Assert.Equal(expected, wrapped, 0.01);
        });
    }

    /// <summary>
    /// <c>.ac lin 1001 0 20k</c> is a reasonable thing to type and the simulator runs it, putting
    /// one DC point in front of an otherwise good sweep. Before this was handled the import got as
    /// far as <c>GroupDelay.PhaseDelayOnGrid</c>, which refused the whole file with "Phase delay is
    /// undefined at or below 0 Hz. (Parameter 'freqHz')" -- a DSP parameter name in place of an
    /// explanation. The point is left out instead, because DC is the one part of the sweep that
    /// could never be drawn on a logarithmic axis anyway.
    /// </summary>
    [Fact]
    public void Inspect_OfASweepThatStartsAtZeroHz_LeavesTheDcPointOutAndSaysSo()
    {
        double[] swept = [.. Enumerable.Range(1, 200).Select(i => i * 100.0)];
        double[] freqHz = [0.0, .. swept];
        string raw = WriteAcSweep("from-dc.raw", freqHz, OnePole(freqHz));

        WithService(service =>
        {
            var info = service.Inspect(raw);

            Assert.Equal(swept.Length, info.PointCount);
            Assert.Equal(100.0, info.StartFreqHz, 1e-9);
            Assert.Contains(info.Warnings, w => w.Contains("0 Hz", StringComparison.Ordinal));

            // And the sweep is still described as what it is -- a linear one. Before the slice,
            // every figure derived from the axis was poisoned by the zero: IsRatioConstant bails
            // out on a non-positive first point, so points-per-decade read 0.0.
            Assert.False(info.IsLogSpaced);
            Assert.True(info.PointsPerDecade > 0.0);
        });
    }

    [Fact]
    public async Task Import_OfASweepThatStartsAtZeroHz_StoresTheRestUnchanged()
    {
        double[] swept = [.. Enumerable.Range(1, 200).Select(i => i * 100.0)];
        string withDc = WriteAcSweep("dc-in.raw", [0.0, .. swept], OnePole([0.0, .. swept]));
        string without = WriteAcSweep("dc-out.raw", swept, OnePole(swept));

        await WithHarnessAsync(async h =>
        {
            var sliced = await h.GainAsync(await h.ImportAsync(withDc, PeakReferenced()));
            var plain = await h.GainAsync(await h.ImportAsync(without, PeakReferenced()));

            // Identical to the same sweep simulated without the DC point: the slice removes one
            // point and changes nothing else, delay curves included.
            Assert.Equal(swept.Length, sliced.FreqHz.Length);
            Assert.Equal(plain.FreqHz, sliced.FreqHz);
            Assert.Equal(plain.MagnitudeMaxDb, sliced.MagnitudeMaxDb);
            Assert.Equal(plain.PhaseDeg, sliced.PhaseDeg);
            Assert.NotNull(sliced.PhaseDelayS);
            Assert.Equal(plain.ThreeDb.UpperHz, sliced.ThreeDb.UpperHz, 1e-9);
        });
    }

    /// <summary>A file with nothing above DC is not a sweep, and says that rather than naming a span of zero.</summary>
    [Fact]
    public void Inspect_OfASweepThatIsNothingButDc_IsRefused()
    {
        double[] freqHz = [0.0, 0.0, 0.0];
        string raw = WriteAcSweep("all-dc.raw", freqHz, OnePole(freqHz));

        WithService(service =>
        {
            var ex = Assert.Throws<FormatException>(() => service.Inspect(raw));
            Assert.Contains("not a sweep", ex.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// The seam is reported in the <i>file's</i> own numbering, which is what the user is looking
    /// at. The slice shifts every index behind it, so the skipped points have to be added back.
    /// </summary>
    [Fact]
    public void Inspect_OfASteppedSweepBehindADcPoint_NamesThePointInTheFilesOwnNumbering()
    {
        double[] sweep = LogSweep(10.0, 1000.0, 20);
        double[] freqHz = [0.0, .. sweep, .. sweep];
        string raw = WriteAcSweep("dc-stepped.raw", freqHz, OnePole(freqHz));

        WithService(service =>
        {
            var ex = Assert.Throws<FormatException>(() => service.Inspect(raw));
            Assert.Contains(".step", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"point {sweep.Length + 1}", ex.Message, StringComparison.Ordinal);
        });
    }

    // ---- stepped runs ----

    /// <summary>
    /// The oracle for a stepped run is arithmetic, which is why the fixtures step the source
    /// amplitude: an AC analysis is a linearisation, so stepping an <c>AC {acmag}</c> source 1/2/3
    /// must offset the whole curve by exactly 0, 20log10(2) and 20log10(3) dB and leave the phase
    /// untouched. Measured on the generated LTspice and QSpice files, that is what both do, to four
    /// decimals — so the same shape here checks the splitting against a number rather than against
    /// itself.
    /// </summary>
    [Fact]
    public async Task Import_OfASteppedRun_MakesOneResultPerSweepAtItsOwnLevel()
    {
        double[] one = LogSweep(10.0, 100_000.0, 20);
        double[] freqHz = [.. one, .. one, .. one];
        var pole = OnePole(one);
        Complex[] output = [.. pole, .. pole.Select(c => c * 2.0), .. pole.Select(c => c * 3.0)];
        string raw = WriteAcSweep("stepped.raw", freqHz, output, flags: SteppedFlags);

        await WithHarnessAsync(async h =>
        {
            var ids = await h.ImportAllAsync(raw, PeakReferenced());
            Assert.Equal(3, ids.Count);

            var levels = new List<double>();
            foreach (long id in ids)
            {
                var gain = await h.GainAsync(id);
                Assert.Equal(one.Length, gain.FreqHz.Length);
                Assert.Equal(one[0], gain.FreqHz[0], one[0] * 1e-5);
                levels.Add(gain.MagnitudeMaxDb[0]);
            }

            // The stored magnitude is absolute (dB re 1 V), so the three results differ by exactly
            // the two logarithms -- which is the whole oracle: each sweep was cut out at the right
            // place and analysed on its own, rather than the file's first sweep being read three
            // times. The tolerance is f32 storage, not arithmetic.
            Assert.Equal(20.0 * Math.Log10(2.0), levels[1] - levels[0], 1e-4);
            Assert.Equal(20.0 * Math.Log10(3.0), levels[2] - levels[0], 1e-4);

            // Each one's own reference moved with it, so a peak-referenced corner is unchanged
            // across the three -- the curves are the same shape at three levels.
            var corners = new List<double>();
            foreach (long id in ids)
                corners.Add((await h.GainAsync(id)).ThreeDb.UpperHz);

            Assert.Equal(corners[0], corners[1], corners[0] * 1e-6);
            Assert.Equal(corners[0], corners[2], corners[0] * 1e-6);
        });
    }

    /// <summary>
    /// Several results of one file have to be told apart in the History list, and the only place
    /// the step values exist is the simulator's log — the <c>.raw</c> header says <c>stepped</c> and
    /// nothing more. LTspice writes one <c>.step freq=1000</c> line per step; QSpice writes no log
    /// at all, which is what the numbered fallback is for.
    /// </summary>
    [Fact]
    public async Task Import_OfASteppedRun_TakesEachResultsNameFromTheLog()
    {
        double[] one = LogSweep(10.0, 10_000.0, 10);
        double[] freqHz = [.. one, .. one];
        var pole = OnePole(one);
        Complex[] output = [.. pole, .. pole];

        string labelled = WriteAcSweep("labelled.raw", freqHz, output, flags: SteppedFlags);
        File.WriteAllText(
            Path.ChangeExtension(labelled, ".log"),
            "Circuit: whatever\n.step ra=22000\n.step ra=23000\nTotal elapsed time: 1.3 seconds.\n");

        string bare = WriteAcSweep("bare.raw", freqHz, output, flags: SteppedFlags);

        await WithHarnessAsync(async h =>
        {
            var withLog = await h.ImportAllAsync(labelled, PeakReferenced(), name: "Amp");
            var withoutLog = await h.ImportAllAsync(bare, PeakReferenced(), name: "Amp");

            Assert.Equal(["Amp - ra=22000", "Amp - ra=23000"], await h.NamesAsync(withLog));
            Assert.Equal(["Amp - step 1 of 2", "Amp - step 2 of 2"], await h.NamesAsync(withoutLog));
        });
    }

    [Fact]
    public async Task Import_OfChosenSweeps_ImportsOnlyThose()
    {
        double[] one = LogSweep(10.0, 10_000.0, 10);
        double[] freqHz = [.. one, .. one, .. one];
        var pole = OnePole(one);
        Complex[] output = [.. pole, .. pole, .. pole];
        string raw = WriteAcSweep("subset.raw", freqHz, output, flags: SteppedFlags);

        await WithHarnessAsync(async h =>
        {
            var ids = await h.ImportAllAsync(raw, PeakReferenced(), name: "Amp", sweeps: [2, 0]);

            // Sweep order, not the order they were asked for: a History list sorted by anything
            // else would show step 3 above step 1.
            Assert.Equal(["Amp - step 1 of 3", "Amp - step 3 of 3"], await h.NamesAsync(ids));

            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => h.ImportAllAsync(raw, PeakReferenced(), sweeps: [3]));
            Assert.Contains("no sweep 4", ex.Message, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// The point cap is per sweep, because <c>No. Points</c> is the grand total over every step: a
    /// cap applied to it refuses a file whose own sweeps are perfectly ordinary. 771 points stepped
    /// 85 times reaches 65 535, which is the ceiling.
    /// </summary>
    [Fact]
    public void Inspect_OfManySmallSweeps_IsNotRefusedForTheirTotal()
    {
        double[] one = LogSweep(1.0, 100.0, 4);
        var freqHz = new List<double>();
        for (int s = 0; s < 40; s++)
            freqHz.AddRange(one);

        string raw = WriteAcSweep("many.raw", [.. freqHz], OnePole([.. freqHz]), flags: SteppedFlags);

        WithService(service =>
        {
            var info = service.Inspect(raw);

            Assert.Equal(40, info.Sweeps.Count);
            Assert.Equal(one.Length, info.PointCount);
            Assert.All(info.Sweeps, s => Assert.Equal(one.Length, s.PointCount));
            Assert.Contains(info.Warnings, w => w.Contains("40 sweeps", StringComparison.Ordinal));
        });
    }

    // ---- an .ac run read as an impedance ----

    /// <summary>
    /// The oracle is arithmetic again: a series R–L is <c>Z = R + j2πfL</c> in closed form at every
    /// frequency, so the magnitude, the phase <i>and</i> the resonance-free shape are all checkable
    /// against a formula rather than against the app's own output. Driven by a 1 A source, which is
    /// also the case that proves requiring the current trace costs nothing — its column is a
    /// constant 1, and dividing by it is the identity.
    /// </summary>
    [Theory]
    [InlineData(false)]  // LTspice: a complex abscissa
    [InlineData(true)]   // QSpice:  a bare f64 abscissa
    public async Task ImportImpedance_OfASeriesResistorAndInductor_ReadsTheFormula(bool qspice)
    {
        const double ohms = 6.8;
        const double henry = 0.6e-3;

        double[] freqHz = LogSweep(10.0, 100_000.0, 50);
        Complex[] current = [.. freqHz.Select(_ => Complex.One)];
        Complex[] voltage = [.. freqHz.Select(f => new Complex(ohms, 2.0 * Math.PI * f * henry))];

        string raw = WriteAcSweep($"rl-{qspice}.raw", freqHz, voltage, qspice: qspice, current: current);

        await WithHarnessAsync(async h =>
        {
            long resultId = Assert.Single(await h.ImportImpedanceAsync(raw, voltageIndex: 1, currentIndex: 2));
            var z = await h.ImpedanceAsync(resultId);

            Assert.Equal(freqHz.Length, z.FreqHz.Length);
            for (int k = 0; k < freqHz.Length; k++)
            {
                var expected = new Complex(ohms, 2.0 * Math.PI * freqHz[k] * henry);

                // Stored as dB relative to 1 ohm -- the impedance convention, not a gain.
                Assert.Equal(20.0 * Math.Log10(expected.Magnitude), z.MagnitudeMaxDb[k], 1e-3);
                Assert.Equal(expected.Phase * 180.0 / Math.PI, z.PhaseDeg[k], 1e-3);
                Assert.Equal(1.0, z.Coherence[k], 1e-6);
            }

            // At 10 Hz the coil contributes 0.038 ohm of 6.8, so the bottom of the sweep is the
            // resistor to three decimals -- which is what makes the top end the interesting half.
            Assert.Equal(ohms, Math.Pow(10.0, z.MagnitudeMaxDb[0] / 20.0), 1e-3);
        });
    }

    /// <summary>
    /// A simulator's source current flows into its own + terminal, so <c>V/I</c> comes out half a
    /// turn round — a flat 6.8 Ω load reads −6.8 Ω at ±180°, which is exactly the swapped-channel
    /// signature the app already detects on a bench measurement. There it can only warn, because
    /// the fix is rewiring; here the fix is a sign and the data proves which one, so Auto corrects
    /// it and says so.
    /// </summary>
    [Fact]
    public async Task ImportImpedance_WithTheCurrentTheOtherWayRound_FlipsItAndSaysSo()
    {
        double[] freqHz = LogSweep(20.0, 20_000.0, 20);
        Complex[] backwards = [.. freqHz.Select(_ => -Complex.One)];
        Complex[] voltage = [.. freqHz.Select(_ => new Complex(6.8, 0.0))];

        string raw = WriteAcSweep("backwards.raw", freqHz, voltage, current: backwards);

        await WithHarnessAsync(async h =>
        {
            var auto = await h.ImpedanceAsync(Assert.Single(
                await h.ImportImpedanceAsync(raw, 1, 2, name: "auto")));
            var asStored = await h.ImpedanceAsync(Assert.Single(
                await h.ImportImpedanceAsync(raw, 1, 2, direction: AcImpedanceCurrentDirection.AsStored,
                    name: "as stored")));

            // Auto: a real 6.8 ohm resistor at 0 degrees.
            Assert.Equal(20.0 * Math.Log10(6.8), auto.MagnitudeMaxDb[0], 1e-3);
            Assert.Equal(0.0, auto.PhaseDeg[0], 1e-3);

            // AsStored: the same magnitude at half a turn, which is the reading the flip exists to
            // avoid -- correct arithmetic about a physically impossible load.
            Assert.Equal(20.0 * Math.Log10(6.8), asStored.MagnitudeMaxDb[0], 1e-3);
            Assert.Equal(180.0, Math.Abs(asStored.PhaseDeg[0]), 1e-3);

            var stored = await h.StoredImpedanceSimulationAsync("auto");
            Assert.Contains(
                stored.Source.Warnings,
                w => w.Contains("negative real part", StringComparison.Ordinal));
        });
    }

    /// <summary>
    /// An impedance is volts over amps, so a second voltage trace is refused by name rather than
    /// divided into a dimensionless number that would be stored and drawn as ohms.
    /// </summary>
    [Fact]
    public async Task ImportImpedance_WithTwoVoltages_IsRefusedByUnit()
    {
        double[] freqHz = LogSweep(20.0, 20_000.0, 10);
        string raw = WriteAcSweep("units.raw", freqHz, OnePole(freqHz), reference: OnePole(freqHz));

        await WithHarnessAsync(async h =>
        {
            var ex = await Assert.ThrowsAsync<FormatException>(
                () => h.ImportImpedanceAsync(raw, voltageIndex: 1, currentIndex: 2));

            Assert.Contains("V(in)", ex.Message, StringComparison.Ordinal);
            Assert.Contains("volts over amps", ex.Message, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// The resonance is what an impedance curve is read for, and <c>BuildPoints</c> finds it on the
    /// log axis the simulator wrote rather than on a uniform one. A parallel RLC peaks at
    /// <c>1/(2π√(LC))</c>, which is a number to check against.
    /// </summary>
    [Fact]
    public async Task ImportImpedance_OfAParallelResonance_PutsThePeakWhereTheFormulaDoes()
    {
        const double ohms = 40.0;
        const double henry = 10e-3;
        const double farad = 253.3e-6;   // 1/(2*pi*sqrt(LC)) = 100.0 Hz

        double[] freqHz = LogSweep(10.0, 1000.0, 200);
        Complex[] current = [.. freqHz.Select(_ => Complex.One)];
        Complex[] voltage =
        [
            .. freqHz.Select(f =>
            {
                double w = 2.0 * Math.PI * f;
                Complex y = (1.0 / ohms) + (Complex.One / new Complex(0.0, w * henry)) + new Complex(0.0, w * farad);
                return Complex.One / y;
            }),
        ];

        string raw = WriteAcSweep("resonance.raw", freqHz, voltage, current: current);

        await WithHarnessAsync(async h =>
        {
            long resultId = Assert.Single(await h.ImportImpedanceAsync(raw, 1, 2));
            var rows = await h.RowsAsync(resultId);

            var z = Assert.Single(rows);
            Assert.Equal(ResponseTraceKind.Impedance, z.Kind);

            double resonanceHz = 1.0 / (2.0 * Math.PI * Math.Sqrt(henry * farad));
            Assert.Equal(resonanceHz, z.PeakHz, resonanceHz * 0.01);
            Assert.Equal(20.0 * Math.Log10(ohms), z.PeakDb, 0.01);

            // The bandwidth columns are zeros, as every impedance row's are: corners are a response
            // concept and the reader branches on the kind.
            Assert.Equal(0.0, z.OneDb.LowerHz);
            Assert.Equal(0.0, z.ThreeDb.UpperHz);
        });
    }

    // ---- helpers ----

    /// <summary>What LTspice writes for a stepped AC run, verbatim.</summary>
    private const string SteppedFlags = "complex double forward log nocompression stepped";

    private static AcSimulationImportRequest PeakReferenced() => new(
        Path: "", OutputTraceIndex: 0, ReferenceTraceIndex: null, LevelOffsetDb: 0.0,
        AnalysisLowHz: 0.0, AnalysisHighHz: double.MaxValue,
        Reference: BandwidthReferenceSpec.Peak, Name: null, Notes: null);

    private static int NearestTo(StoredResponseTrace trace, double freqHz) =>
        Enumerable.Range(0, trace.FreqHz.Length).MinBy(i => Math.Abs(trace.FreqHz[i] - freqHz));

    /// <summary>A log-spaced sweep, the way <c>.ac dec</c> writes one.</summary>
    private static double[] LogSweep(double startHz, double endHz, int perDecade)
    {
        double decades = Math.Log10(endHz / startHz);
        int count = (int)Math.Round(decades * perDecade) + 1;
        var freqHz = new double[count];
        for (int i = 0; i < count; i++)
            freqHz[i] = startHz * Math.Pow(10.0, i / (double)perDecade);
        return freqHz;
    }

    /// <summary>1 / (1 + j f/fc) — the one transfer function whose corner needs no measuring.</summary>
    private static Complex[] OnePole(double[] freqHz) =>
        [.. freqHz.Select(f => Complex.One / new Complex(1.0, f / CornerHz))];

    /// <summary>
    /// Writes a complex <c>.raw</c> in either dialect: LTspice's UTF-16 header with a complex
    /// abscissa, or QSpice's ASCII header with a bare f64 one.
    /// </summary>
    private string WriteAcSweep(
        string name,
        double[] freqHz,
        Complex[] output,
        Complex[]? reference = null,
        bool qspice = false,
        string plotname = "AC Analysis",
        string? flags = null,
        Complex[]? current = null)
    {
        var columns = new List<(string Name, string Type, Complex[] Values)>
        {
            ("V(out)", "voltage", output),
        };
        if (reference is not null)
            columns.Add(("V(in)", "voltage", reference));
        if (current is not null)
            columns.Add(("I(V1)", "device_current", current));

        int variables = columns.Count + 1;
        int points = freqHz.Length;
        bool bareAbscissa = qspice;

        var header = new StringBuilder();
        header.Append(CultureInfo.InvariantCulture, $"Title: * {name}\n");
        header.Append("Date: Sun Oct 11 21:03:50 2026\n");
        header.Append(CultureInfo.InvariantCulture, $"Plotname: {plotname}\n");
        header.Append(CultureInfo.InvariantCulture,
            $"Flags: {flags ?? (qspice ? "complex" : "complex double forward log nocompression")}\n");
        header.Append(CultureInfo.InvariantCulture, $"No. Variables: {variables}\n");
        header.Append(CultureInfo.InvariantCulture, $"No. Points: {points,12}\n");
        header.Append("Offset:   0.0000000000000000e+00\n");
        header.Append(qspice
            ? "Command: QSPICE64, Build Oct 11 2026 08:32:21\n"
            : "Command: Linear Technology Corporation LTspice\n");
        header.Append("Variables:\n");
        header.Append(CultureInfo.InvariantCulture, $"\t0\t{(qspice ? "Frequency" : "frequency")}\tfrequency\n");
        for (int v = 0; v < columns.Count; v++)
            header.Append(CultureInfo.InvariantCulture, $"\t{v + 1}\t{columns[v].Name}\t{columns[v].Type}\n");
        header.Append("Binary:\n");

        var encoding = qspice ? (Encoding)Encoding.Latin1 : Encoding.Unicode;
        string path = Path.Combine(_dir, name);
        using var stream = File.Create(path);
        stream.Write(encoding.GetBytes(header.ToString()));

        int abscissaBytes = bareAbscissa ? 8 : 16;
        int bytesPerPoint = abscissaBytes + (columns.Count * 16);
        var point = new byte[bytesPerPoint];
        for (int p = 0; p < points; p++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(point.AsSpan(0), freqHz[p]);
            if (!bareAbscissa)
                BinaryPrimitives.WriteDoubleLittleEndian(point.AsSpan(8), 0.0);

            for (int v = 0; v < columns.Count; v++)
            {
                int at = abscissaBytes + (v * 16);
                BinaryPrimitives.WriteDoubleLittleEndian(point.AsSpan(at), columns[v].Values[p].Real);
                BinaryPrimitives.WriteDoubleLittleEndian(point.AsSpan(at + 8), columns[v].Values[p].Imaginary);
            }

            stream.Write(point);
        }

        return path;
    }

    private sealed class Harness(
        SimulationResponseImportService service,
        MeasurementRepository measurements,
        JsonSerializerOptions serializerOptions,
        long typeId)
    {
        public SimulationResponseImportService Service { get; } = service;
        public MeasurementRepository Measurements { get; } = measurements;
        public long TypeId { get; } = typeId;

        /// <summary>
        /// One sweep's result, which is what all but the stepped cases want — and
        /// <see cref="Assert.Single{T}(IEnumerable{T})"/> is doing a second job here: every one of
        /// those cases writes a single-sweep fixture, so a file that unexpectedly split into
        /// several fails here rather than silently having its first sweep asserted.
        /// </summary>
        public async Task<long> ImportAsync(
            string path, AcSimulationImportRequest template, bool referenceTrace = false, string? name = null) =>
            Assert.Single(await ImportAllAsync(path, template, referenceTrace, name));

        public Task<IReadOnlyList<long>> ImportAllAsync(
            string path,
            AcSimulationImportRequest template,
            bool referenceTrace = false,
            string? name = null,
            IReadOnlyList<int>? sweeps = null) =>
            Service.ImportAsync(
                TypeId,
                template with
                {
                    Path = path,
                    OutputTraceIndex = 1,
                    ReferenceTraceIndex = referenceTrace ? 2 : null,
                    Name = name,
                    SweepIndices = sweeps,
                },
                CancellationToken.None);

        public async Task<IReadOnlyList<StoredResponseTrace>> RowsAsync(long resultId) =>
            await Measurements.GetResponseTracesAsync(resultId, CancellationToken.None);

        public async Task<StoredResponseTrace> GainAsync(long resultId) =>
            (await RowsAsync(resultId)).Single(r => r.Kind == ResponseTraceKind.Gain);

        public Task<IReadOnlyList<long>> ImportImpedanceAsync(
            string path,
            int voltageIndex,
            int currentIndex,
            AcImpedanceCurrentDirection direction = AcImpedanceCurrentDirection.Auto,
            string? name = null) =>
            Service.ImportImpedanceAsync(
                TypeId,
                new AcImpedanceImportRequest(path, voltageIndex, currentIndex, direction, name, Notes: null),
                CancellationToken.None);

        public async Task<StoredResponseTrace> ImpedanceAsync(long resultId) =>
            (await RowsAsync(resultId)).Single(r => r.Kind == ResponseTraceKind.Impedance);

        /// <summary>The AC block out of an imported impedance's own <c>params_json</c>.</summary>
        public async Task<AcSimulationParameters> StoredImpedanceSimulationAsync(string name)
        {
            var listed = await Measurements.ListResultsAsync(
                new MeasurementFilter(MeasurementTypeId: TypeId), CancellationToken.None);
            long id = listed.Single(r => r.Name == name).ResultId;
            string paramsJson = (await Measurements.GetResultAsync(id, CancellationToken.None))!.ParamsJson;
            return JsonSerializer.Deserialize<ImpedanceParameters>(paramsJson, serializerOptions)!.AcSimulation!;
        }

        public async Task<IReadOnlyList<string>> NamesAsync(IReadOnlyList<long> resultIds)
        {
            var names = new List<string>();
            foreach (long id in resultIds)
                names.Add((await Measurements.GetResultAsync(id, CancellationToken.None))!.Name);

            return names;
        }

        public AcSimulationAnalysis ReadAnalysis(string paramsJson) =>
            AcSimulationAnalysis.FromParamsJson(paramsJson, serializerOptions)!;
    }

    private static void WithService(Action<SimulationResponseImportService> body)
    {
        var jsonOptions = new JsonOptions();
        jsonOptions.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        body(new SimulationResponseImportService(
            new MeasurementRepository(new SqliteConnectionFactory(new TonebenchDatabaseOptions { Path = ":memory:" })),
            Options.Create(jsonOptions),
            NullLogger<SimulationResponseImportService>.Instance));
    }

    private static async Task WithHarnessAsync(Func<Harness, Task> body)
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"tonebench-ac-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new TonebenchDatabaseOptions { Path = dbPath });
            await using (var connection = await factory.OpenAsync(CancellationToken.None))
                await new MigrationRunner().RunAsync(connection, CancellationToken.None);

            var jsonOptions = new JsonOptions();
            jsonOptions.SerializerOptions.Converters.Add(new JsonStringEnumConverter());

            var measurements = new MeasurementRepository(factory);
            var projects = new ProjectRepository(factory);
            long projectId = await projects.CreateProjectAsync("ac", null, CancellationToken.None);
            long typeId = await projects.CreateTypeAsync(
                projectId, "FrequencyResponse", "response", null, "{}", CancellationToken.None);

            await body(new Harness(
                new SimulationResponseImportService(
                    measurements, Options.Create(jsonOptions),
                    NullLogger<SimulationResponseImportService>.Instance),
                measurements,
                jsonOptions.SerializerOptions,
                typeId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string candidate in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }
    }
}
