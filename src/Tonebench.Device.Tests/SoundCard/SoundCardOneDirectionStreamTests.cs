using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.Device.SoundCard;

namespace Tonebench.Device.Tests.SoundCard;

/// <summary>
/// One-direction streams — stage 1 of <c>docs/separate in out interfaces plan.md</c>.
///
/// <para>The case they exist for is two devices that are not one interface: a capture-only ADC
/// (an E1DA Cosmos has only a USB-C out), a playback-only USB DAC, or a QA403 capturing while a
/// sound card plays. Before this, <c>Pa_OpenStream</c> was always handed two parameter blocks and
/// the device refused to connect unless both sides were chosen, so a capture-only card appeared in
/// the picker and then threw — which <c>collapseToBestApi</c>'s own comment had predicted.</para>
///
/// <para>Note what is deliberately <b>not</b> asserted here: that two devices in one stream drift.
/// That is a property of two clocks rather than of this code, and the point of a one-direction
/// stream is that each one free-runs on its own device's clock instead of being read and written
/// in lockstep against two.</para>
/// </summary>
public class SoundCardOneDirectionStreamTests
{
    [Fact]
    public void ARequestWithNoDirection_CarriesBothDirections()
    {
        var request = new SoundCardStreamRequest(0, 2, 1, 2, 48_000.0, Exclusive: true);

        // The default matters: every caller that predates one-direction streams keeps duplex
        // without being edited, which is what made this change small.
        Assert.Equal(SoundCardStreamDirection.Duplex, request.Direction);
        Assert.True(request.HasInput);
        Assert.True(request.HasOutput);
    }

    [Theory]
    [InlineData(true, true, SoundCardStreamDirection.Duplex)]
    [InlineData(true, false, SoundCardStreamDirection.InputOnly)]
    [InlineData(false, true, SoundCardStreamDirection.OutputOnly)]
    public void TheDirection_FollowsWhichDevicesWereChosen(bool input, bool output, SoundCardStreamDirection expected)
    {
        var settings = new SoundCardSettings
        {
            InputHostApi = input ? "ASIO" : string.Empty,
            InputDeviceName = input ? "In" : string.Empty,
            OutputHostApi = output ? "ASIO" : string.Empty,
            OutputDeviceName = output ? "Out" : string.Empty,
        };

        Assert.Equal(expected, settings.Direction);
        Assert.Equal(input, settings.HasInput);
        Assert.Equal(output, settings.HasOutput);
        Assert.Equal(input && output, settings.IsConfigured);
        Assert.True(settings.IsUsable);
    }

    [Fact]
    public void WithNothingChosenAtAll_TheBenchIsStillNotUsable()
    {
        // IsUsable is "can a stream be opened", and neither direction cannot.
        Assert.False(new SoundCardSettings().IsUsable);
    }

    [Fact]
    public async Task ACaptureOnlyCard_Connects()
    {
        var (device, _) = BuildCaptureOnly();

        DeviceInfo info = await device.ConnectAsync(CancellationToken.None);

        // Only the device that exists is named. Naming an output as well would print an empty
        // string for a device that was never chosen.
        Assert.Contains("Cosmos ADC", info.ProductName);
        Assert.DoesNotContain("+", info.ProductName);
    }

    [Fact]
    public async Task APlaybackOnlyCard_Connects()
    {
        var (device, _) = BuildPlaybackOnly();

        DeviceInfo info = await device.ConnectAsync(CancellationToken.None);

        Assert.Contains("D50 III", info.ProductName);
        Assert.DoesNotContain("+", info.ProductName);
    }

    [Fact]
    public async Task WithNothingChosen_ConnectIsRefused()
    {
        var pa = new FakePortAudio();
        var device = new SoundCardDevice(
            new SoundCardSettings(), SoundCardCalibration.Uncalibrated,
            NullLogger<SoundCardDevice>.Instance, pa);

        await Assert.ThrowsAsync<SoundCardNotFoundException>(
            () => device.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ACaptureOnlyStream_HandsOutBlocksAndNeverWrites()
    {
        var (device, pa) = BuildCaptureOnly();
        pa.NextStream.InputSample = (_, _) => 0.5f;
        await device.ConnectAsync(CancellationToken.None);

        int blocks = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (PcmBlock block in device.StreamAsync(new AcquisitionConfig(48_000.0, 2), cts.Token))
        {
            block.Dispose();
            if (++blocks >= 3)
                break;
        }

        Assert.Equal(3, blocks);

        // The whole point: nothing was written. A zeroed output parameter block would have named
        // device 0 with 0 channels and PortAudio would have been asked to play to it.
        Assert.Equal(0, pa.NextStream.WriteCount);
        Assert.True(pa.NextStream.ReadCount >= 3);
        Assert.Equal(SoundCardStreamDirection.InputOnly, pa.OpenedWith!.Direction);
    }

    [Fact]
    public async Task APlaybackOnlyStream_PlaysAndHandsOutNoBlocks()
    {
        var (device, pa) = BuildPlaybackOnly();

        // The write is the only blocking call there is, so it has to pace the pump the way a real
        // driver's full ring does -- otherwise the loop spins as fast as the CPU allows.
        pa.NextStream.PaceWrites = true;
        await device.ConnectAsync(CancellationToken.None);

        var tone = new ChannelGeneratorConfig(Enabled: true, Tones: [new GeneratorTone(1_000.0, -6.0)]);
        await device.ConfigureGeneratorAsync(new GeneratorConfig(tone, tone), CancellationToken.None);

        int blocks = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        try
        {
            await foreach (PcmBlock block in device.StreamAsync(new AcquisitionConfig(48_000.0, 2), cts.Token))
            {
                block.Dispose();
                blocks++;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: an output-only stream never completes on its own, because nothing it does
            // produces a block to end on.
        }

        // A generator wearing an IAnalyzerDevice's interface -- which is exactly what the split
        // device will need of it.
        Assert.Equal(0, blocks);
        Assert.Equal(0, pa.NextStream.ReadCount);
        Assert.True(
            pa.NextStream.WriteCount > 0,
            "an output-only stream that writes nothing is not playing anything");
        Assert.Equal(SoundCardStreamDirection.OutputOnly, pa.OpenedWith!.Direction);
    }

    [Fact]
    public void TheRateProbe_ForACaptureOnlyCard_AsksForCaptureOnly()
    {
        var pa = new FakePortAudio();
        var api = pa.AddHostApi("WASAPI", SoundCardHostApiKind.Wasapi);
        SoundCardDeviceDescriptor input = pa.AddDevice(api, "Cosmos ADC", inputs: 2, outputs: 0);

        var seen = new List<SoundCardStreamRequest>();
        pa.FormatSupported = r =>
        {
            seen.Add(r);
            return r.SampleRateHz == 192_000.0;
        };

        var rates = new SoundCardEnumerator(pa).SupportedRatesHz(input, output: null, exclusive: true);

        Assert.Equal([192_000.0], rates);
        Assert.NotEmpty(seen);
        Assert.All(seen, r =>
        {
            Assert.Equal(SoundCardStreamDirection.InputOnly, r.Direction);
            Assert.False(r.HasOutput);
            Assert.Equal(input.Index, r.InputDevice);
        });
    }

    [Fact]
    public void TheRateProbe_WithNeitherDirection_IsRefused()
    {
        // Answering with the full candidate list would report a bench that cannot stream at all as
        // able to stream at every rate.
        var pa = new FakePortAudio();

        Assert.Throws<ArgumentException>(
            () => new SoundCardEnumerator(pa).SupportedRatesHz(input: null, output: null, exclusive: true));
    }

    private static (SoundCardDevice Device, FakePortAudio Pa) BuildCaptureOnly()
    {
        var pa = new FakePortAudio();
        var api = pa.AddHostApi("WASAPI", SoundCardHostApiKind.Wasapi);
        pa.AddDevice(api, "Cosmos ADC", inputs: 2, outputs: 0);

        var settings = new SoundCardSettings
        {
            InputHostApi = "WASAPI",
            InputDeviceName = "Cosmos ADC",
        };

        return (new SoundCardDevice(
            settings, SoundCardCalibration.Uncalibrated,
            NullLogger<SoundCardDevice>.Instance, pa), pa);
    }

    private static (SoundCardDevice Device, FakePortAudio Pa) BuildPlaybackOnly()
    {
        var pa = new FakePortAudio();
        var api = pa.AddHostApi("WASAPI", SoundCardHostApiKind.Wasapi);
        pa.AddDevice(api, "D50 III", inputs: 0, outputs: 2);

        var settings = new SoundCardSettings
        {
            OutputHostApi = "WASAPI",
            OutputDeviceName = "D50 III",
        };

        return (new SoundCardDevice(
            settings, SoundCardCalibration.Uncalibrated,
            NullLogger<SoundCardDevice>.Instance, pa), pa);
    }
}
