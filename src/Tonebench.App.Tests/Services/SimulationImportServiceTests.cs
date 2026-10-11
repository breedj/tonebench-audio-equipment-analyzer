using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tonebench.App.Endpoints;
using Tonebench.App.Streaming;
using Tonebench.App.Services;
using Tonebench.Data;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// The LTspice simulation import, end to end: a synthesized <c>.raw</c> in, a stored measurement
/// result out.
///
/// <para>The anchor is
/// <see cref="Import_OfAToneWithOnePerCentSecondHarmonic_ReadsThdOfOnePerCent"/>. It is the same
/// golden case the live analysis path has to satisfy (CLAUDE.md's "Fundamental + 1% H2 -> THD reads
/// 1.000%"), asserted through the parser, the uniform regrid, the anti-alias decimation, the
/// analysis and the database -- so the one number that matters cannot be quietly wrong at any step
/// in between.</para>
/// </summary>
public class SimulationImportServiceTests : IDisposable
{
    private const double NativeRateHz = 1_000_000.0;
    private const int Points = 95_000;      // 95 ms at 1 us, the reference run's own shape
    private const double FundamentalHz = 1_000.0;

    private readonly string _dir = Directory.CreateTempSubdirectory("tonebench-sim-").FullName;

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

    [Fact]
    public async Task Import_OfAToneWithOnePerCentSecondHarmonic_ReadsThdOfOnePerCent()
    {
        string raw = WriteTransient("thd.raw", Tone(h2: 0.01));

        await WithHarnessAsync(async h =>
        {
            long typeId = await h.CreateFftTypeAsync();
            long resultId = await h.Import.ImportAsync(typeId, Request(raw), CancellationToken.None);

            var detail = (await h.Measurements.GetResultAsync(resultId, CancellationToken.None))!;
            double thd = detail.MetricsByChannel[0]["thd_percent"];
            double fundamental = detail.MetricsByChannel[0]["fundamental_hz"];

            Assert.Equal(1.0, thd, 0.01);
            Assert.Equal(FundamentalHz, fundamental, 12.0); // within one bin of a 10.5 Hz grid
        });
    }

    /// <summary>
    /// The level offset is applied at analysis time, which is what makes it changeable later. It
    /// must move the measured level by exactly itself and leave the distortion alone -- it is a
    /// claim about the probe, not about the circuit.
    /// </summary>
    [Fact]
    public async Task Import_WithALevelOffset_MovesTheLevelByExactlyThatAndLeavesThdAlone()
    {
        string raw = WriteTransient("offset.raw", Tone(h2: 0.01));

        await WithHarnessAsync(async h =>
        {
            long typeId = await h.CreateFftTypeAsync();
            long plain = await h.Import.ImportAsync(typeId, Request(raw), CancellationToken.None);
            long shifted = await h.Import.ImportAsync(
                typeId, Request(raw, levelOffsetDb: -20.0, name: "shifted"), CancellationToken.None);

            var a = (await h.Measurements.GetResultAsync(plain, CancellationToken.None))!.MetricsByChannel[0];
            var b = (await h.Measurements.GetResultAsync(shifted, CancellationToken.None))!.MetricsByChannel[0];

            Assert.Equal(a["fundamental_dbv"] - 20.0, b["fundamental_dbv"], 0.001);
            Assert.Equal(a["thd_percent"], b["thd_percent"], 0.0001);
        });
    }

    /// <summary>
    /// A tube plate node carries its operating point -- a few hundred volts under a signal of a
    /// few -- and both halves of handling that are measured rather than assumed.
    ///
    /// <para>Left on, the DC leaks out of any window far enough to swamp the bottom of the
    /// measurement band: the same capture reads <b>THD+N 8373 % and SNR -38.5 dB</b>, which is
    /// arithmetically correct and describes nothing anyone wants to know. And because f32 precision
    /// is relative to the largest value present, storing the rail costs the signal its resolution:
    /// the distortion floor goes from <b>0.00000029 % to 0.00015 %</b>, a factor of 517, which is
    /// why the mean comes off before the quantisation rather than after it.</para>
    /// </summary>
    [Fact]
    public async Task Import_OfATraceOnALargeDcRail_RemovesTheRailAndStillMeasuresTheSignal()
    {
        var onRail = Tone(h2: 0.01);
        for (int i = 0; i < onRail.Length; i++)
            onRail[i] += 300.0;

        string raw = WriteTransient("plate.raw", onRail);

        await WithHarnessAsync(async h =>
        {
            long typeId = await h.CreateFftTypeAsync();
            long resultId = await h.Import.ImportAsync(typeId, Request(raw), CancellationToken.None);

            var detail = (await h.Measurements.GetResultAsync(resultId, CancellationToken.None))!;
            var m = detail.MetricsByChannel[0];

            // The signal is measured as if it sat around zero.
            Assert.Equal(1.0, m["thd_percent"], 0.01);
            Assert.Equal(-3.01, m["fundamental_dbv"], 0.05);

            // And the noise figures mean something again -- they are nowhere near the 8373 % and
            // -38.5 dB the rail produced.
            Assert.InRange(m["thd_n_percent"], 0.0, 5.0);
            Assert.InRange(m["snr_db"], 20.0, 200.0);

            // The operating point is recorded rather than discarded.
            var simulation = Deserialize(detail.ParamsJson).Simulation!;
            Assert.Equal(300.0, Assert.Single(simulation.RemovedDcV!), 0.001);
        });
    }

    [Fact]
    public async Task Import_OfBothChannels_StoresTwoSpectraAndTwoSetsOfMetrics()
    {
        string raw = WriteTransient("stereo.raw", Tone(h2: 0.01), Tone(h2: 0.05));

        await WithHarnessAsync(async h =>
        {
            long typeId = await h.CreateFftTypeAsync();
            long resultId = await h.Import.ImportAsync(
                typeId, Request(raw, rightTraceIndex: 2), CancellationToken.None);

            var detail = (await h.Measurements.GetResultAsync(resultId, CancellationToken.None))!;
            Assert.Equal([0, 1], detail.AvailableChannels);
            Assert.Equal(1.0, detail.MetricsByChannel[0]["thd_percent"], 0.01);
            Assert.Equal(5.0, detail.MetricsByChannel[1]["thd_percent"], 0.05);
        });
    }

    /// <summary>
    /// QSpice writes the same container as LTspice, and a <c>.qraw</c> has to import exactly as a
    /// <c>.raw</c> does. Three differences, all measured on real QSpice files and all reproduced by
    /// this fixture: the header is plain ASCII rather than UTF-16; there is an <c>Abscissa:</c>
    /// line and a block of <c>.param</c>/<c>.alias</c> lines LTspice never writes; and -- the one
    /// that matters -- <b>every column is float64 while the flags say only <c>real</c></b>, so a
    /// reader that trusts the <c>double</c> flag takes each value from four bytes into the wrong
    /// field and hands back plausible-looking nonsense.
    /// </summary>
    [Fact]
    public async Task Import_OfAQSpiceFile_ReadsItExactlyAsAnLtspiceOne()
    {
        string raw = WriteTransient("AudioAmp.qraw", Tone(h2: 0.01), qspice: true);

        await WithHarnessAsync(async h =>
        {
            var info = h.Import.Inspect(raw);
            Assert.Equal("Transient Analysis", info.Plotname);
            Assert.Equal(["V(out)"], info.Traces.Select(t => t.Name));

            long typeId = await h.CreateFftTypeAsync();
            long resultId = await h.Import.ImportAsync(typeId, Request(raw), CancellationToken.None);

            var detail = (await h.Measurements.GetResultAsync(resultId, CancellationToken.None))!;
            Assert.Equal(1.0, detail.MetricsByChannel[0]["thd_percent"], 0.01);
            Assert.Equal(MeasurementSource.Simulation, detail.Source);
        });
    }

    /// <summary>
    /// A simulator left to choose its own steps takes picosecond ones through an edge and tens of
    /// microseconds through a quiet stretch -- measured on QSpice's own AudioAmp example, 6.25 ps
    /// against 42.5 us, seven orders of magnitude apart. Resampling at 1/median there is 160 GHz,
    /// i.e. sixteen billion samples for a tenth of a second: an allocation that cannot succeed, for
    /// detail a hundred thousand times above the audio band. The rate is bounded against the
    /// <i>coarsest</i> step instead.
    /// </summary>
    [Fact]
    public void ResampleRate_OnAnAdaptiveRun_IsBoundedByTheCoarsestStepRatherThanTheMedian()
    {
        var axis = new TimeAxis(
            StartS: 0.0, EndS: 0.1,
            MinStepS: 2.05e-12, MedianStepS: 6.25e-12, MaxStepS: 42.5e-6,
            IsMonotonic: true, FirstNonMonotonicIndex: -1);

        Assert.Equal(160e9, axis.NativeRateHz, 1e9);   // what the median alone would ask for
        Assert.True(axis.IsAdaptive(20_000.0));

        double rate = axis.ResampleRateHz(20_000.0);
        Assert.InRange(rate, 1e6, 2e6);                // 64 / 42.5 us
        Assert.InRange(rate * 0.1, 0, 5e6);            // and an affordable sample count

        // A uniform run is untouched: 1 us steps stay at 1 MHz.
        var uniform = axis with { MinStepS = 1e-6, MedianStepS = 1e-6, MaxStepS = 1e-6 };
        Assert.Equal(1e6, uniform.ResampleRateHz(20_000.0), 1.0);
        Assert.False(uniform.IsAdaptive(20_000.0));
    }

    /// <summary>
    /// The refusal that earns its place. Measured on a real run saved without
    /// <c>.options plotwinsize=0</c>: 5.7 % of the points survive and THD reads 0.626 % against a
    /// true 0.378 %. It does not look broken, it looks like a worse amplifier -- so the message has
    /// to name the directive rather than let a plausible number through.
    /// </summary>
    [Fact]
    public void Inspect_OfACompressedRun_IsRefusedAndNamesTheDirective()
    {
        // 95 ms in 1600 points, with the worst gap at 537 us -- the real compressed file's shape.
        string raw = WriteTransient("compressed.raw", Tone(h2: 0.0), compressed: true);

        WithService(service =>
        {
            var ex = Assert.Throws<FormatException>(() => service.Inspect(raw));
            Assert.Contains("plotwinsize", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("too coarsely sampled", ex.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Theory]
    [InlineData("Operating Point", "operating-point")]
    [InlineData("AC Analysis", "AC analysis")]
    [InlineData("FFT of time domain data", "FFT export")]
    public void Inspect_OfSomethingThatIsNotATransientRun_SaysWhatItIs(string plotname, string expected)
    {
        string raw = WriteTransient($"wrong-{plotname.GetHashCode(StringComparison.Ordinal)}.raw",
            Tone(h2: 0.0), plotname: plotname);

        WithService(service =>
        {
            var ex = Assert.Throws<FormatException>(() => service.Inspect(raw));
            Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Inspect_ListsEveryTraceWithItsUnit_AndReportsTheTimeAxis()
    {
        string raw = WriteTransient("inspect.raw", Tone(h2: 0.01), Tone(h2: 0.0), withCurrent: true);

        WithService(service =>
        {
            var info = service.Inspect(raw);

            Assert.Equal(LtspiceAnalysisNames.Transient, info.Plotname);
            Assert.Equal(Points, info.PointCount);
            Assert.Equal((Points - 1) / NativeRateHz, info.DurationS, 1e-9);
            Assert.Equal(NativeRateHz, info.NativeRateHz, 1.0);
            Assert.Equal(["V(out)", "V(in)", "I(R1)"], info.Traces.Select(t => t.Name));
            Assert.Equal(["V", "V", "A"], info.Traces.Select(t => t.Unit));

            // Nothing wrong with the file itself -- it is uncompressed and double precision. The
            // one warning is about its *length*: 95 ms is 10.5 Hz bins, which is coarse against a
            // 20 Hz band edge. Asserted as exactly that rather than relaxed to "some warnings", so
            // a compression or precision warning appearing here would still fail.
            string only = Assert.Single(info.Warnings);
            Assert.Contains("resolution", only, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// Resolution is 1 divided by the saved length and nothing in the app can improve it, so a run
    /// too short for the band has to say so while the fix -- simulate for longer -- is still
    /// available. Judged against the <em>bottom</em> of the band, which is what binds: at 10.5 Hz
    /// bins a 20 Hz band edge is two bins in, while 1 kHz is ninety-five.
    /// </summary>
    [Theory]
    // 95 ms -> 10.5 Hz bins. Against a 20 Hz band edge that is 1.9 bins: too coarse.
    [InlineData(95_000, 20.0, true, "too coarse")]
    // The same run against a band starting at 100 Hz is 9.5 bins in: nothing to say.
    [InlineData(95_000, 100.0, false, null)]
    // 400 ms -> 2.5 Hz bins, 8 bins below 20 Hz: comfortable.
    [InlineData(400_000, 20.0, false, null)]
    // 250 ms -> 4 Hz bins, 5 bins in: still comfortable.
    [InlineData(250_000, 20.0, false, null)]
    public void Inspect_WhenTheRunIsTooShortForTheBand_SaysSoAndSaysWhatWouldBeEnough(
        int points, double bandLoHz, bool expectWarning, string? expectedPhrase)
    {
        string raw = WriteTransient($"res{points}-{bandLoHz}.raw", Tone(h2: 0.0, points: points));

        WithService(service =>
        {
            var info = service.Inspect(raw, measurementHiFreqHz: 20_000.0, measurementLoFreqHz: bandLoHz);
            string? warning = info.Warnings.FirstOrDefault(w => w.Contains("resolution", StringComparison.OrdinalIgnoreCase));

            if (!expectWarning)
            {
                Assert.Null(warning);
                return;
            }

            Assert.NotNull(warning);
            Assert.Contains(expectedPhrase!, warning, StringComparison.OrdinalIgnoreCase);
            // It has to name the length that would do, not just complain.
            Assert.Contains("200 ms", warning, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// A coarse run is a warning, never a refusal: a short simulation is perfectly good for looking
    /// at harmonics of a 1 kHz tone, which is most of what these are used for.
    /// </summary>
    [Fact]
    public async Task Import_OfAShortRun_StillSucceeds_WithTheWarningRecordedOnTheResult()
    {
        string raw = WriteTransient("short.raw", Tone(h2: 0.01, points: 95_000));

        await WithHarnessAsync(async h =>
        {
            long typeId = await h.CreateFftTypeAsync();
            long resultId = await h.Import.ImportAsync(typeId, Request(raw), CancellationToken.None);

            var detail = (await h.Measurements.GetResultAsync(resultId, CancellationToken.None))!;
            Assert.Equal(1.0, detail.MetricsByChannel[0]["thd_percent"], 0.01);

            var warnings = Deserialize(detail.ParamsJson).Simulation!.Source.Warnings;
            Assert.Contains(warnings, w => w.Contains("resolution", StringComparison.OrdinalIgnoreCase));
        });
    }

    /// <summary>
    /// The test that catches the level offset or the analysis rate having been baked into the
    /// stored samples by mistake: re-analysing at the settings it was imported with has to produce
    /// the identical spectrum, bin for bin.
    /// </summary>
    [Fact]
    public async Task Reanalyze_AtTheSameSettings_ReproducesTheStoredSpectrumExactly()
    {
        string raw = WriteTransient("reanalyze-same.raw", Tone(h2: 0.01));

        await WithHarnessAsync(async h =>
        {
            long typeId = await h.CreateFftTypeAsync();
            long resultId = await h.Import.ImportAsync(
                typeId, Request(raw, levelOffsetDb: -6.0), CancellationToken.None);

            var before = await ReadSpectrumAsync(h, resultId);
            bool ok = await h.Import.ReanalyzeAsync(resultId, new SimulationReanalyzeRequest(), CancellationToken.None);
            var after = await ReadSpectrumAsync(h, resultId);

            Assert.True(ok);
            Assert.Equal(before.Length, after.Length);
            for (int i = 0; i < before.Length; i++)
                Assert.Equal(before[i], after[i]);
        });
    }

    [Fact]
    public async Task Reanalyze_WithADifferentWindow_RewritesTheSpectrumAndSaysSoInParamsJson()
    {
        string raw = WriteTransient("reanalyze-window.raw", Tone(h2: 0.01));

        await WithHarnessAsync(async h =>
        {
            long typeId = await h.CreateFftTypeAsync();
            long resultId = await h.Import.ImportAsync(typeId, Request(raw), CancellationToken.None);

            var before = await ReadSpectrumAsync(h, resultId);
            await h.Import.ReanalyzeAsync(
                resultId, new SimulationReanalyzeRequest(Window: WindowType.BlackmanHarris), CancellationToken.None);
            var after = await ReadSpectrumAsync(h, resultId);

            Assert.NotEqual(before, after);

            var detail = (await h.Measurements.GetResultAsync(resultId, CancellationToken.None))!;
            var parameters = Deserialize(detail.ParamsJson);
            Assert.Equal(WindowType.BlackmanHarris, parameters.Window);
            Assert.NotNull(parameters.Simulation);

            // Still 1 % of H2: a different window is a different estimate of the same circuit.
            Assert.Equal(1.0, detail.MetricsByChannel[0]["thd_percent"], 0.02);
        });
    }

    /// <summary>The samples are the durable part, so re-analysis must not touch them.</summary>
    [Fact]
    public async Task Reanalyze_LeavesTheStoredSamplesAlone()
    {
        string raw = WriteTransient("reanalyze-pcm.raw", Tone(h2: 0.01));

        await WithHarnessAsync(async h =>
        {
            long typeId = await h.CreateFftTypeAsync();
            long resultId = await h.Import.ImportAsync(typeId, Request(raw), CancellationToken.None);
            long captureId = (await h.Measurements.GetResultAsync(resultId, CancellationToken.None))!.CaptureId;

            var before = await h.Measurements.GetCapturePcmAsync(captureId, CancellationToken.None);
            await h.Import.ReanalyzeAsync(
                resultId, new SimulationReanalyzeRequest(FftSize: 4096), CancellationToken.None);
            var after = await h.Measurements.GetCapturePcmAsync(captureId, CancellationToken.None);

            Assert.NotNull(before);
            Assert.NotNull(after);
            Assert.Equal(before.SampleRateHz, after.SampleRateHz);
            Assert.Equal(before.Channels[0], after.Channels[0]);
        });
    }

    [Fact]
    public async Task Reanalyze_OfAMeasuredResult_IsRefusedRatherThanGuessed()
    {
        await WithHarnessAsync(async h =>
        {
            Assert.False(await h.Import.ReanalyzeAsync(4242, new SimulationReanalyzeRequest(), CancellationToken.None));
        });
    }

    /// <summary>
    /// Moving the start time on a re-analysis has to <i>skip into</i> the stored samples, not
    /// merely shorten the record it takes from their front.
    ///
    /// <para>This is the one re-appliable setting that cannot be read off the arithmetic alone.
    /// The analysis rate is derived from the remaining duration, so a later start produces a
    /// shorter record and a correct-looking bin width either way -- while reading from the first
    /// stored sample trims the <i>end</i> of the run, which is the opposite of what "analyse from"
    /// says and would quietly keep the startup settling it exists to remove.</para>
    ///
    /// <para>So the fixture's two halves carry different tones rather than different levels: the
    /// fundamental the analysis reports names which half it read, with no arithmetic in
    /// between.</para>
    /// </summary>
    [Fact]
    public async Task Reanalyze_FromALaterStart_AnalysesTheLaterPartOfTheRun()
    {
        string raw = WriteTransient("reanalyze-start.raw", TwoHalves());

        await WithHarnessAsync(async h =>
        {
            long typeId = await h.CreateFftTypeAsync();
            long resultId = await h.Import.ImportAsync(typeId, Request(raw), CancellationToken.None);

            var imported = (await h.Measurements.GetResultAsync(resultId, CancellationToken.None))!;
            Assert.Equal(FundamentalHz, imported.MetricsByChannel[0]["fundamental_hz"], 25.0);

            Assert.True(await h.Import.ReanalyzeAsync(
                resultId, new SimulationReanalyzeRequest(StartTimeS: 0.050), CancellationToken.None));

            var after = (await h.Measurements.GetResultAsync(resultId, CancellationToken.None))!;
            Assert.Equal(3.0 * FundamentalHz, after.MetricsByChannel[0]["fundamental_hz"], 25.0);
        });
    }

    /// <summary>
    /// The samples begin where the import left them, so a start time earlier than that asks for
    /// audio nothing has. Refused naming where they do begin, rather than clamped -- a clamp
    /// answers a different question than the one asked and says nothing about it.
    /// </summary>
    [Fact]
    public async Task Reanalyze_FromBeforeTheImportsOwnStart_IsRefusedNamingWhereTheSamplesBegin()
    {
        string raw = WriteTransient("reanalyze-early.raw", Tone(h2: 0.01));

        await WithHarnessAsync(async h =>
        {
            long typeId = await h.CreateFftTypeAsync();
            long resultId = await h.Import.ImportAsync(
                typeId, Request(raw, startTimeS: 0.020), CancellationToken.None);

            var refused = await Assert.ThrowsAsync<ArgumentException>(() => h.Import.ReanalyzeAsync(
                resultId, new SimulationReanalyzeRequest(StartTimeS: 0.0), CancellationToken.None));

            Assert.Contains("20.000 ms", refused.Message);
        });
    }

    /// <summary>
    /// A prefilled dialog sends the stored start time straight back, and that must never be read
    /// as asking for something earlier than the samples -- the stored figure is the samples' own
    /// start, so the comparison has to carry at least the rounding it was written with.
    /// </summary>
    [Fact]
    public async Task Reanalyze_AtTheStoredStartTime_IsAccepted()
    {
        string raw = WriteTransient("reanalyze-same-start.raw", Tone(h2: 0.01));

        await WithHarnessAsync(async h =>
        {
            long typeId = await h.CreateFftTypeAsync();
            long resultId = await h.Import.ImportAsync(
                typeId, Request(raw, startTimeS: 0.020), CancellationToken.None);

            Assert.True(await h.Import.ReanalyzeAsync(
                resultId, new SimulationReanalyzeRequest(StartTimeS: 0.020), CancellationToken.None));
        });
    }

    /// <summary>
    /// Where the samples begin is recorded separately from where the analysis starts, and only the
    /// second one moves. Asserted by going <i>back</i>: a stored start that drifted forward with
    /// each re-analysis would refuse the second call here, having forgotten that the earlier
    /// samples are still there.
    /// </summary>
    [Fact]
    public async Task Reanalyze_DoesNotMoveWhereTheStoredSamplesBegin()
    {
        string raw = WriteTransient("reanalyze-pcmstart.raw", TwoHalves());

        await WithHarnessAsync(async h =>
        {
            long typeId = await h.CreateFftTypeAsync();
            long resultId = await h.Import.ImportAsync(typeId, Request(raw), CancellationToken.None);

            await h.Import.ReanalyzeAsync(
                resultId, new SimulationReanalyzeRequest(StartTimeS: 0.050), CancellationToken.None);

            var moved = (await h.Measurements.GetResultAsync(resultId, CancellationToken.None))!;
            var simulation = Deserialize(moved.ParamsJson).Simulation!;
            Assert.Equal(0.050, simulation.StartTimeS, 1e-9);
            Assert.Equal(0.0, simulation.PcmStartS!.Value, 1e-9);

            // And back, which only the separate figure makes possible.
            Assert.True(await h.Import.ReanalyzeAsync(
                resultId, new SimulationReanalyzeRequest(StartTimeS: 0.0), CancellationToken.None));

            var back = (await h.Measurements.GetResultAsync(resultId, CancellationToken.None))!;
            Assert.Equal(FundamentalHz, back.MetricsByChannel[0]["fundamental_hz"], 25.0);
        });
    }

    /// <summary>
    /// The import bounds the FFT size against the file; a re-analysis has no file, so it bounds it
    /// against what is left of the samples after the start time. Beyond that the record would be
    /// interpolating past the simulator's own resolution rather than measuring it.
    /// </summary>
    [Fact]
    public async Task Reanalyze_AtAnFftSizeTheSamplesCannotFill_IsRefusedNamingTheLargest()
    {
        string raw = WriteTransient("reanalyze-toobig.raw", Tone(h2: 0.01));

        await WithHarnessAsync(async h =>
        {
            long typeId = await h.CreateFftTypeAsync();
            long resultId = await h.Import.ImportAsync(typeId, Request(raw), CancellationToken.None);

            var refused = await Assert.ThrowsAsync<ArgumentException>(() => h.Import.ReanalyzeAsync(
                resultId, new SimulationReanalyzeRequest(FftSize: 1 << 20), CancellationToken.None));

            // 95 000 samples, so 65536 is the largest power of two they can fill.
            Assert.Contains("65536", refused.Message);
        });
    }

    /// <summary>
    /// An imported simulation writes no <c>generator_dbv</c> metric, so a measurement holding only
    /// simulations makes that correlated subquery NULL for every row of the listing -- the
    /// documented Dapper trap, which materialises an untyped NULL column as <c>byte[]</c> and fails
    /// with "a parameterless default constructor ... is required", naming nothing that is wrong.
    /// The IFNULL sentinel already covers it; this is what keeps it covered.
    /// </summary>
    [Fact]
    public async Task ListResults_OfATypeHoldingOnlySimulations_DoesNotTripTheDapperNullColumnTrap()
    {
        string raw = WriteTransient("listing.raw", Tone(h2: 0.01));

        await WithHarnessAsync(async h =>
        {
            long typeId = await h.CreateFftTypeAsync();
            await h.Import.ImportAsync(typeId, Request(raw), CancellationToken.None);

            var items = await h.Measurements.ListResultsAsync(
                new MeasurementFilter(MeasurementTypeId: typeId), CancellationToken.None);

            var only = Assert.Single(items);
            Assert.Equal(MeasurementSource.Simulation, only.Source);
            Assert.Equal(1.0, only.ChannelHeadlines[0].ThdPercent, 0.01);
        });
    }

    /// <summary>
    /// The endpoint's own DTO has to carry <c>source</c>, not just the domain record and the
    /// repository. Threading a field through the data layer and forgetting the response record is
    /// a silent default rather than an error -- the list simply renders every imported simulation
    /// as a measured run, with no badge and no section, and nothing anywhere says why. Found
    /// exactly that way in the browser.
    ///
    /// <para>Asserted by mapping through <c>ToSummaryResponse</c>/<c>BuildDetailResponse</c>'s own
    /// record shapes rather than by reading the repository, because the repository was never the
    /// half that was wrong.</para>
    /// </summary>
    [Fact]
    public async Task TheResponseRecords_CarryTheSimulationMarker_NotJustTheDatabaseRow()
    {
        string raw = WriteTransient("wire.raw", Tone(h2: 0.01));

        await WithHarnessAsync(async h =>
        {
            long typeId = await h.CreateFftTypeAsync();
            long resultId = await h.Import.ImportAsync(typeId, Request(raw), CancellationToken.None);

            var summary = Assert.Single(await h.Measurements.ListResultsAsync(
                new MeasurementFilter(MeasurementTypeId: typeId), CancellationToken.None));
            var detail = (await h.Measurements.GetResultAsync(resultId, CancellationToken.None))!;

            var summaryResponse = new MeasurementSummaryResponse(
                summary.ResultId, summary.ProjectName, summary.MeasurementTypeName, summary.Name,
                summary.Notes, summary.TimestampUtc, summary.SampleRateHz, summary.FftSize,
                summary.Channels, summary.ChannelHeadlines, summary.Source);
            Assert.Equal(MeasurementSource.Simulation, summaryResponse.Source);

            // Through the endpoint's own builder, so the *mapping* is covered and not only the
            // record's shape: the first attempt at this added the fields to the record and left
            // the constructor call passing none of them, and `source` came back null on the wire
            // while the summary carried it correctly.
            var pipeline = new SpectrumPipeline(
                new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance);
            var built = (await MeasurementsEndpoints.BuildDetailResponseAsync(
                resultId, h.Measurements, pipeline, CancellationToken.None))!;
            Assert.Equal(MeasurementSource.Simulation, built.Source);
            Assert.NotNull(built.PcmSampleRateHz);

            var detailResponse = new MeasurementDetailResponse(
                detail.ResultId, detail.MeasurementTypeId, detail.ProjectName, detail.MeasurementTypeName,
                detail.Kind, detail.Name, detail.Notes, detail.TimestampUtc, detail.ParamsJson,
                detail.SampleRateHz, detail.FftSize, detail.Channels,
                detail.InRangeDbv, detail.OutRangeDbv, detail.GenJson, [],
                detail.Source, detail.PcmSampleRateHz);
            Assert.Equal(MeasurementSource.Simulation, detailResponse.Source);
            Assert.NotNull(detailResponse.PcmSampleRateHz);
        });
    }

    [Fact]
    public async Task Import_NamesTheResultAfterTheFileAndTheTrace_WhenNoNameIsGiven()
    {
        string raw = WriteTransient("EL84 PP.raw", Tone(h2: 0.01));

        await WithHarnessAsync(async h =>
        {
            long typeId = await h.CreateFftTypeAsync();
            long resultId = await h.Import.ImportAsync(typeId, Request(raw, name: null), CancellationToken.None);

            var detail = (await h.Measurements.GetResultAsync(resultId, CancellationToken.None))!;
            Assert.Equal("EL84 PP - V(out)", detail.Name);
            Assert.Equal(MeasurementSource.Simulation, detail.Source);
            Assert.Equal(NativeRateHz, detail.PcmSampleRateHz!.Value, 1.0);
        });
    }

    /// <summary>
    /// The bin width follows the simulation's duration and nothing else, which is the one thing
    /// about this measurement a user has to understand: a bigger FFT raises the analysis rate and
    /// computes more empty spectrum above the band, and buys no resolution at all.
    /// </summary>
    [Theory]
    [InlineData(4096)]
    [InlineData(8192)]
    [InlineData(16384)]
    public void BinWidth_FollowsTheDurationAndNotTheFftSize(int fftSize)
    {
        var source = new SimulationSource(
            "x.raw", "t", null, "Transient Analysis", [], "V(out)", "V", null, null,
            NativeRateHz, Points, 0.0, 0.095, []);
        var spec = new SimulationAnalysis(source, 0.0, 0.0, fftSize, WindowType.Hann, 20.0, 20_000.0, false);

        Assert.Equal(1.0 / 0.095, spec.BinWidthHz, 1e-9);
        Assert.Equal(fftSize / 0.095, spec.BaseAnalysisRateHz, 1e-6);
        // The point of the theory: three FFT sizes, one bin width.
    }

    // ---- helpers ----

    private static SimulationImportRequest Request(
        string path,
        int? rightTraceIndex = null,
        double levelOffsetDb = 0.0,
        string? name = "sim",
        int fftSize = 8192,
        double startTimeS = 0.0) =>
        new(path, LeftTraceIndex: 1, RightTraceIndex: rightTraceIndex, StartTimeS: startTimeS,
            LevelOffsetDb: levelOffsetDb, FftSize: fftSize, Window: WindowType.Hann,
            MeasurementLoFreqHz: 20.0, MeasurementHiFreqHz: 20_000.0, Name: name, Notes: null);

    /// <summary>
    /// A run whose two halves hold different tones -- 1 kHz for the first 50 ms, 3 kHz a tenth as
    /// loud for the remaining 45 ms. Different <i>frequencies</i> rather than different levels, so
    /// the reported fundamental names which half an analysis read with no arithmetic in between;
    /// and the second one quieter, so an analysis of the whole run still reports the first.
    /// </summary>
    private static double[] TwoHalves(int points = Points)
    {
        var x = new double[points];
        int split = (points / 2) + (points % 2);
        for (int n = 0; n < x.Length; n++)
        {
            double t = n / NativeRateHz;
            x[n] = n < split
                ? Math.Sin(2 * Math.PI * FundamentalHz * t)
                : 0.1 * Math.Sin(2 * Math.PI * 3.0 * FundamentalHz * t);
        }
        return x;
    }

    private static double[] Tone(double h2, int points = Points)
    {
        var x = new double[points];
        for (int n = 0; n < x.Length; n++)
        {
            double t = n / NativeRateHz;
            x[n] = Math.Sin(2 * Math.PI * FundamentalHz * t)
                 + (h2 * Math.Sin(2 * Math.PI * 2 * FundamentalHz * t));
        }
        return x;
    }

    private static async Task<double[]> ReadSpectrumAsync(Harness h, long resultId)
    {
        var detail = (await h.Measurements.GetResultAsync(resultId, CancellationToken.None))!;
        var spectrum = await h.Measurements.GetSpectrumAsync(detail.CaptureId, 0, CancellationToken.None);
        return spectrum!.Dbv;
    }

    private static FftSpectrumParameters Deserialize(string json)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Deserialize<FftSpectrumParameters>(json, options)!;
    }

    /// <summary>
    /// Writes a transient <c>.raw</c> in the layout measured off real LTspice output: a UTF-16LE
    /// header, then f64 time plus f64 values interleaved (the <c>double</c> flag).
    /// </summary>
    private string WriteTransient(
        string name,
        double[] left,
        double[]? right = null,
        string plotname = "Transient Analysis",
        bool compressed = false,
        bool withCurrent = false,
        bool qspice = false)
    {
        var columns = new List<(string Name, string Type, double[] Values)>();
        double[] time;

        if (compressed)
        {
            // The same 95 ms, but with the step distribution a compressed run has: mostly coarse,
            // with the worst gap far too wide to carry the audio band.
            var t = new List<double>();
            double at = 0.0;
            var random = new Random(7);
            while (at < 0.095)
            {
                t.Add(at);
                at += 1e-5 + (random.NextDouble() * 527e-6);
            }
            time = [.. t];
            var resampled = new double[time.Length];
            for (int i = 0; i < time.Length; i++)
                resampled[i] = left[Math.Min(left.Length - 1, (int)(time[i] * NativeRateHz))];
            columns.Add(("V(out)", "voltage", resampled));
        }
        else
        {
            time = new double[left.Length];
            for (int i = 0; i < time.Length; i++)
                time[i] = i / NativeRateHz;
            columns.Add(("V(out)", "voltage", left));
        }

        if (right is not null)
            columns.Add(("V(in)", "voltage", right));
        if (withCurrent)
            columns.Add(("I(R1)", "device_current", new double[time.Length]));

        int variables = columns.Count + 1;
        int points = time.Length;

        var header = new StringBuilder();
        header.Append(CultureInfo.InvariantCulture, $"Title: * {name}\n");
        header.Append("Date: Sun Oct 11 11:34:26 2026\n");
        header.Append(CultureInfo.InvariantCulture, $"Plotname: {plotname}\n");
        header.Append(qspice
            ? "Flags: real\n"
            : $"Flags: real double forward{(compressed ? "" : " nocompression")}\n");
        header.Append(CultureInfo.InvariantCulture, $"No. Variables: {variables}\n");
        header.Append(CultureInfo.InvariantCulture, $"No. Points: {points,12}\n");
        header.Append("Offset:   0.0000000000000000e+00\n");
        if (qspice)
        {
            // Lines LTspice never writes, which the reader has to step over.
            header.Append(CultureInfo.InvariantCulture,
                $"Abscissa: {0.0:0.000000000000000e+00} {(points - 1) / NativeRateHz:0.000000000000000e+00}\n");
            header.Append("Command: QSPICE64, Build Oct  6 2026 12:09:27\n");
            header.Append(".param X=15\n");
            header.Append(".alias I(R26) (0.1mho*V(sgnd,0))\n");
        }
        else
        {
            header.Append("Command: Linear Technology Corporation LTspice\n");
        }
        header.Append("Variables:\n");
        header.Append("\t0\ttime\ttime\n");
        for (int v = 0; v < columns.Count; v++)
            header.Append(CultureInfo.InvariantCulture, $"\t{v + 1}\t{columns[v].Name}\t{columns[v].Type}\n");
        header.Append("Binary:\n");

        string path = Path.Combine(_dir, name);
        using var stream = File.Create(path);
        stream.Write((qspice ? Encoding.Latin1 : Encoding.Unicode).GetBytes(header.ToString()));

        var point = new byte[variables * 8];
        for (int p = 0; p < points; p++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(point.AsSpan(0), time[p]);
            for (int v = 0; v < columns.Count; v++)
                BinaryPrimitives.WriteDoubleLittleEndian(point.AsSpan((v + 1) * 8), columns[v].Values[p]);
            stream.Write(point);
        }

        return path;
    }

    private sealed class Harness(
        SimulationImportService import, MeasurementRepository measurements, ProjectRepository projects)
    {
        public SimulationImportService Import { get; } = import;
        public MeasurementRepository Measurements { get; } = measurements;
        public ProjectRepository Projects { get; } = projects;

        private long? _projectId;

        public async Task<long> CreateFftTypeAsync()
        {
            _projectId ??= await Projects.CreateProjectAsync("sim", null, CancellationToken.None);
            return await Projects.CreateTypeAsync(
                _projectId.Value, "FFTSpectrum", "fft", null, "{}", CancellationToken.None);
        }
    }

    private static void WithService(Action<SimulationImportService> body)
    {
        var jsonOptions = new JsonOptions();
        jsonOptions.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        body(new SimulationImportService(
            new MeasurementRepository(new SqliteConnectionFactory(new TonebenchDatabaseOptions { Path = ":memory:" })),
            Options.Create(jsonOptions),
            NullLogger<SimulationImportService>.Instance));
    }

    private static async Task WithHarnessAsync(Func<Harness, Task> body)
    {
        string path = Path.Combine(Path.GetTempPath(), $"tonebench-sim-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new TonebenchDatabaseOptions { Path = path });
            await using (var connection = await factory.OpenAsync(CancellationToken.None))
                await new MigrationRunner().RunAsync(connection, CancellationToken.None);

            var jsonOptions = new JsonOptions();
            jsonOptions.SerializerOptions.Converters.Add(new JsonStringEnumConverter());

            var measurements = new MeasurementRepository(factory);
            var import = new SimulationImportService(
                measurements, Options.Create(jsonOptions), NullLogger<SimulationImportService>.Instance);

            await body(new Harness(import, measurements, new ProjectRepository(factory)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }
    }
}

internal static class LtspiceAnalysisNames
{
    internal const string Transient = "Transient Analysis";
}
