using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Endpoints;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Device.Simulation;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// The experimental 384 kHz rate: what the setting gates, and what is refused at it regardless.
///
/// <para>384 kHz is <b>input-only on a QA403</b>, measured — see
/// <see cref="DeviceCapabilities.Qa403Experimental"/> for the numbers. The ADC re-clocks correctly;
/// the DAC returns a tone about 12 dB low at roughly 44 % distortion, with enough near-DC content to
/// cycle the device's own output protection relay, which a <c>StreamingStop</c> did not clear. So the
/// generator refusal below is hardware protection, not a quality preference, and the simulator
/// mirrors the limit so this is all testable without a QA403.</para>
/// </summary>
public class Experimental384kTests
{
    private const double ExperimentalRateHz = 384_000.0;

    private static DeviceSessionService NewSession(IAnalyzerDevice device) =>
        new(
            device,
            new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
            NullLogger<DeviceSessionService>.Instance);

    [Fact]
    public void WithTheSettingOff_384kHzIsNotOffered()
    {
        var device = new SimulatedDevice { Allow384kHz = () => false };

        Assert.DoesNotContain(ExperimentalRateHz, device.Capabilities.SampleRatesHz);
        Assert.Equal(DeviceCapabilities.Qa403.SampleRatesHz, device.Capabilities.SampleRatesHz);
    }

    [Fact]
    public void WithNoGateAtAll_384kHzIsNotOffered()
    {
        // The default for every device and every test double: the experimental rate has to be asked
        // for, never arrived at.
        Assert.DoesNotContain(ExperimentalRateHz, new SimulatedDevice().Capabilities.SampleRatesHz);
    }

    [Fact]
    public void WithTheSettingOn_384kHzIsOffered()
    {
        var device = new SimulatedDevice { Allow384kHz = () => true };

        Assert.Contains(ExperimentalRateHz, device.Capabilities.SampleRatesHz);

        // Added to the existing ladder rather than replacing it — a rate list that lost 48 kHz would
        // be a far worse bug than one that never gained 384.
        Assert.Equal([48_000.0, 96_000.0, 192_000.0, 384_000.0], device.Capabilities.SampleRatesHz);
    }

    /// <summary>
    /// The gate is read live, so flipping the setting changes the answer on the
    /// <b>already-constructed</b> device.
    ///
    /// <para>This is the assertion that stops someone "simplifying" <c>Allow384kHz</c> from a
    /// <c>Func</c> into a captured <c>bool</c>. The device is built the moment anything resolves
    /// <see cref="DeviceSessionService"/>, which can happen before the configuration document has
    /// been read; a latched value would then be the default for the whole session, and the user's
    /// saved preference would be written correctly and read too late. That is exactly how the
    /// analyzer-kind preference broke once already — see <c>StartupSettingsLoader</c> — and nothing
    /// about reading the document would look wrong while it happened.</para>
    /// </summary>
    [Fact]
    public void TheGateIsReadLive_NotCapturedWhenTheDeviceIsBuilt()
    {
        bool allowed = false;
        var device = new SimulatedDevice { Allow384kHz = () => allowed };

        Assert.DoesNotContain(ExperimentalRateHz, device.Capabilities.SampleRatesHz);

        allowed = true;

        Assert.Contains(ExperimentalRateHz, device.Capabilities.SampleRatesHz);
    }

    [Fact]
    public async Task WithTheSettingOff_StartingAnAcquisitionAt384kHzIsRefused()
    {
        var session = NewSession(new SimulatedDevice { Allow384kHz = () => false });
        await session.ConnectAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.StartAcquisitionAsync(
                new AcquisitionConfig(ExperimentalRateHz, ChannelCount: 2),
                CancellationToken.None));

        Assert.Contains("cannot run at 384 kHz", error.Message);
    }

    [Fact]
    public async Task WithTheSettingOn_AnAcquisitionAt384kHzStarts()
    {
        var session = NewSession(new SimulatedDevice { Allow384kHz = () => true });
        await session.ConnectAsync(CancellationToken.None);

        await session.StartAcquisitionAsync(
            new AcquisitionConfig(ExperimentalRateHz, ChannelCount: 2),
            CancellationToken.None);

        Assert.True(session.Acquiring);

        await session.ShutdownAsync();
    }

    /// <summary>
    /// A voiced generator at 384 kHz is refused even with the setting on, and the message says why —
    /// this is the hardware-protection half, which the checkbox does not unlock.
    /// </summary>
    [Fact]
    public async Task At384kHz_AVoicedGeneratorIsRefused()
    {
        var session = NewSession(new SimulatedDevice { Allow384kHz = () => true });
        await session.ConnectAsync(CancellationToken.None);
        await session.StartAcquisitionAsync(
            new AcquisitionConfig(ExperimentalRateHz, ChannelCount: 2),
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ConfigureGeneratorAsync(
                new GeneratorConfig(
                    new ChannelGeneratorConfig(true, [new GeneratorTone(1_000.0, -6.0)]),
                    ChannelGeneratorConfig.Off),
                CancellationToken.None));

        Assert.Contains("input-only rate", error.Message);
        Assert.Contains("192 kHz", error.Message);

        await session.ShutdownAsync();
    }

    /// <summary>
    /// Silencing the generator at 384 kHz must still succeed. Stop, and every measurement switch,
    /// disable both channels unconditionally; a refusal there would make leaving the rate impossible
    /// without an error, which is the same reasoning that lets a silent configure through with no
    /// device attached at all.
    /// </summary>
    [Fact]
    public async Task At384kHz_SilencingTheGeneratorIsStillAllowed()
    {
        var session = NewSession(new SimulatedDevice { Allow384kHz = () => true });
        await session.ConnectAsync(CancellationToken.None);
        await session.StartAcquisitionAsync(
            new AcquisitionConfig(ExperimentalRateHz, ChannelCount: 2),
            CancellationToken.None);

        await session.ConfigureGeneratorAsync(GeneratorConfig.Off, CancellationToken.None);

        await session.ShutdownAsync();
    }

    [Fact]
    public async Task At192kHz_TheGeneratorIsUnaffected()
    {
        var session = NewSession(new SimulatedDevice { Allow384kHz = () => true });
        await session.ConnectAsync(CancellationToken.None);
        await session.StartAcquisitionAsync(
            new AcquisitionConfig(192_000.0, ChannelCount: 2),
            CancellationToken.None);

        await session.ConfigureGeneratorAsync(
            new GeneratorConfig(
                new ChannelGeneratorConfig(true, [new GeneratorTone(1_000.0, -6.0)]),
                ChannelGeneratorConfig.Off),
            CancellationToken.None);

        await session.ShutdownAsync();
    }

    /// <summary>
    /// The ceiling is published, so the FFT screen's Generator panel can stand down at a
    /// capture-only rate instead of letting <c>RequireGeneratorRateSupported</c>'s refusal arrive as
    /// a 409 on Run. It is read off the device rather than hard-coded, since a sound card's
    /// generator works at every rate it captures at.
    /// </summary>
    [Fact]
    public void TheGeneratorsRateCeiling_IsPublishedOnTheSession()
    {
        var session = NewSession(new SimulatedDevice { Allow384kHz = () => true });

        Assert.Equal(192_000.0, session.MaxGeneratorSampleRateHz);
    }

    /// <summary>
    /// A device with no ceiling reports <b>null</b>, not
    /// <see cref="double.PositiveInfinity"/> — which is the default
    /// <see cref="IAnalyzerDevice.MaxGeneratorSampleRateHz"/>, i.e. every analyzer but the QA403 and
    /// the simulator.
    ///
    /// <para>And the reason is the whole of this test's second half: <c>System.Text.Json</c> will
    /// not write a non-finite double, and it finds that out part way through the response — the
    /// client would see a 200 with a body that stops mid-object and lose the rate list, the range
    /// ladders and the channel count along with it. Same trap as
    /// <c>ImpedanceStatusJsonTests</c>; nothing else here catches it, because the property reads
    /// perfectly well in C#.</para>
    /// </summary>
    [Fact]
    public void OnAnAnalyzerWithNoCeiling_ItIsNullRatherThanInfinity()
    {
        var session = NewSession(new FakeAnalyzerDevice());

        Assert.Null(session.MaxGeneratorSampleRateHz);

        var options = new JsonOptions();
        options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        var caps = session.Capabilities;
        string json = JsonSerializer.Serialize(
            new DeviceRangesResponse(
                caps.SampleRatesHz, caps.InputRangesDbv, caps.OutputRangesDbv, caps.MaxChannelCount,
                session.MinStimulusFrequencyHz, session.MaxGeneratorSampleRateHz),
            options.SerializerOptions);

        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("maxGeneratorSampleRateHz").ValueKind);
        Assert.Equal(caps.SampleRatesHz.Count, parsed.RootElement.GetProperty("sampleRatesHz").GetArrayLength());
    }
}
