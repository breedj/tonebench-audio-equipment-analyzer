using System.Buffers;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Dsp;

namespace Qa403.App.Tests.Streaming;

/// <summary>
/// The analyzer must never build a spectrum from samples captured before a discontinuity in the
/// signal — an acquisition restart at a new sample rate, a generator change, an input-range
/// change. An FFT record spliced across such a change carries a step, and a step smears the tone
/// into a broad skirt either side of it, so the displayed noise floor jumps tens of dB and then
/// decays over one buffer-fill as the stale samples slide out.
///
/// The bug this pins: <see cref="SpectrumPipeline.Start"/> called EnsureRingBuffers, which
/// allocates fresh buffers only when the FFT size changed. Restarting at a new sample rate keeps
/// the same size, so every pre-restart sample stayed in the buffer — and because the buffer was
/// already full, the very first tick after the restart analyzed almost entirely stale data.
/// </summary>
public class SpectrumPipelineFlushTests
{
    private const int FftSize = 4096;
    private const double SampleRateHz = 48000.0;

    private static SpectrumPipeline NewPipeline(SpectrumBroadcastHub hub) =>
        new(hub, NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions
            {
                FftSize = FftSize,
                TargetFrameRateHz = 100,
                DisplayColumns = 100,
            },
        };

    /// <summary>Feeds <paramref name="sampleCount"/> samples of a single-channel 1 kHz tone.</summary>
    private static void EnqueueTone(SpectrumPipeline pipeline, int sampleCount, double amplitudeV, ref long phase)
    {
        float[] buffer = ArrayPool<float>.Shared.Rent(sampleCount);
        for (int i = 0; i < sampleCount; i++)
            buffer[i] = (float)(amplitudeV * Math.Sin(2.0 * Math.PI * 1000.0 * (phase + i) / SampleRateHz));

        phase += sampleCount;
        pipeline.Enqueue(new PcmBlock(buffer, sampleCount, SampleRateHz, channelCount: 1, sequenceNumber: 0, timestampTicks: 0));
    }

    private static async Task<bool> SawMetersWithinAsync(ChannelReader<byte[]> reader, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                byte[] frame = await reader.ReadAsync(cts.Token);
                if (StreamFrame.DecodeHeader(frame).MsgType == StreamMsgType.Meters)
                    return true;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static void DrainPending(ChannelReader<byte[]> reader)
    {
        while (reader.TryRead(out _))
        {
        }
    }

    [Fact]
    public async Task Restart_DoesNotAnalyzeSamplesCapturedBeforeIt()
    {
        var hub = new SpectrumBroadcastHub();
        var pipeline = NewPipeline(hub);
        var (_, reader) = hub.Subscribe();
        long phase = 0;

        // A full window of "old" signal, and confirmation that this alone produces frames.
        pipeline.Start(SampleRateHz, channelCount: 1);
        EnqueueTone(pipeline, FftSize, amplitudeV: 1.0, ref phase);
        Assert.True(
            await SawMetersWithinAsync(reader, TimeSpan.FromSeconds(5)),
            "a full window should produce a meters frame");
        await pipeline.StopAsync();

        // Restart, as a sample-rate change does. The FFT size is unchanged, so this is exactly the
        // case where the buffers used to be carried over intact.
        pipeline.Start(SampleRateHz, channelCount: 1);
        DrainPending(reader);

        // Half a window of new data: the analyzer must stay silent rather than completing the
        // record with pre-restart samples.
        EnqueueTone(pipeline, FftSize / 2, amplitudeV: 0.1, ref phase);
        Assert.False(
            await SawMetersWithinAsync(reader, TimeSpan.FromMilliseconds(400)),
            "no frame may be broadcast while the window is still part-filled with pre-restart samples");

        // Completing the window with post-restart samples releases frames again.
        EnqueueTone(pipeline, FftSize, amplitudeV: 0.1, ref phase);
        Assert.True(
            await SawMetersWithinAsync(reader, TimeSpan.FromSeconds(5)),
            "a full window of post-restart samples should produce frames again");

        await pipeline.StopAsync();
    }

    [Fact]
    public async Task Flush_DiscardsBufferedSamplesSoAStimulusChangeIsNotAnalyzedAcross()
    {
        var hub = new SpectrumBroadcastHub();
        var pipeline = NewPipeline(hub);
        var (_, reader) = hub.Subscribe();
        long phase = 0;

        pipeline.Start(SampleRateHz, channelCount: 1);
        EnqueueTone(pipeline, FftSize, amplitudeV: 1.0, ref phase);
        Assert.True(await SawMetersWithinAsync(reader, TimeSpan.FromSeconds(5)));

        // What DeviceSessionService does when the generator or input range changes mid-acquisition.
        pipeline.Flush();
        DrainPending(reader);

        EnqueueTone(pipeline, FftSize / 2, amplitudeV: 0.1, ref phase);
        Assert.False(
            await SawMetersWithinAsync(reader, TimeSpan.FromMilliseconds(400)),
            "Flush must stop frames until a whole new window has been captured");

        EnqueueTone(pipeline, FftSize, amplitudeV: 0.1, ref phase);
        Assert.True(await SawMetersWithinAsync(reader, TimeSpan.FromSeconds(5)));

        await pipeline.StopAsync();
    }

    /// <summary>
    /// Emptying the buffers is only half of what a stimulus flush has to do. On real hardware the
    /// change has not reached the DAC when the call returns — a QA403 has one TX queue (~65 ms:
    /// 3200 frames at 48 kHz, 12416 at 192 kHz) of pre-change audio still committed — so the samples
    /// arriving immediately after the flush are the *old* stimulus, and they refill the window with
    /// exactly the step the flush existed to keep out. Measured in loopback on a 1 kHz tone moved
    /// -18 -> -17 dBV with no range change anywhere: the first record after a bare flush read
    /// +16 to +41 dB of broadband floor (and up to +81 dB of skirt around the tone) depending on FFT
    /// size, versus +0.3 dB or better once the in-flight frames were discarded too.
    ///
    /// Asserted structurally rather than spectrally: a discard of N frames must delay the first
    /// frame by N samples, so a window's worth of post-flush samples is *not* enough on its own.
    /// </summary>
    [Fact]
    public async Task Flush_WithDiscardFrames_AlsoDropsTheAudioStillInFlightWhenItWasCalled()
    {
        const int discardFrames = FftSize / 4;

        var hub = new SpectrumBroadcastHub();
        var pipeline = NewPipeline(hub);
        var (_, reader) = hub.Subscribe();
        long phase = 0;

        pipeline.Start(SampleRateHz, channelCount: 1);
        EnqueueTone(pipeline, FftSize, amplitudeV: 1.0, ref phase);
        Assert.True(await SawMetersWithinAsync(reader, TimeSpan.FromSeconds(5)));

        pipeline.Flush(discardFrames);
        DrainPending(reader);

        // A full window — but the first discardFrames of it are the stale ones, so the window is
        // still short and nothing may be broadcast yet. Without the discard this alone would frame.
        EnqueueTone(pipeline, FftSize, amplitudeV: 0.1, ref phase);
        Assert.False(
            await SawMetersWithinAsync(reader, TimeSpan.FromMilliseconds(400)),
            "the discarded frames must not count toward the new window");

        EnqueueTone(pipeline, discardFrames, amplitudeV: 0.1, ref phase);
        Assert.True(
            await SawMetersWithinAsync(reader, TimeSpan.FromSeconds(5)),
            "once a whole window of post-discard samples has arrived, frames resume");

        await pipeline.StopAsync();
    }
}
