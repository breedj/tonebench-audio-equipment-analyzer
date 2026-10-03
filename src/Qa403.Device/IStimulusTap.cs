namespace Qa403.Device;

/// <summary>
/// A sink for the samples a device hands its DAC, set on <see cref="IAnalyzerDevice.StimulusTap"/>
/// while <c>Qa403.App</c>'s stream recorder is armed and null the rest of the time.
///
/// <para><b>Why the output needs a tap at all, and what it can and cannot show.</b> The input stream
/// arrives as <see cref="PcmBlock"/>s that anyone downstream can copy; the output never leaves the
/// transfer loop that builds it. Recording it answers a different question from recording the input:
/// it says what the DUT was actually driven with, which is what a stale-stimulus bug looks like
/// (<c>SimulatedDevice</c>'s one-block latency, a flush that did not outrun the DAC queue, a range
/// change applied after the prime). It <b>cannot</b> show a DAC starvation gap: the bytes submitted
/// here are always continuous, and a gap is the device playing nothing while a transfer is late. The
/// evidence for that is the transfer summary and <c>DiagnosticRecorder</c>'s held-constant scan on
/// the way back in.</para>
///
/// <para><b>This is called from inside the transfer loop</b>, which CLAUDE.md otherwise reserves for
/// transfer management, so an implementation gets one job: copy the span and return. No file I/O, no
/// allocation beyond a pooled rent, no lock anything else holds. The cost is bounded by the span —
/// 512 frames per QA403 chunk, one block per sound-card iteration — against the waveform synthesis
/// the same loop already does for every one of those samples, which is far more work. Still off by
/// default: the fault being chased is often the loop's own timing.</para>
/// </summary>
public interface IStimulusTap
{
    /// <summary>
    /// One buffer of generator output, interleaved, in volts at the analyzer's output terminals —
    /// what the DAC was told to produce, with any clipping against the output range already applied,
    /// and in <b>logical</b> channel order (left first) whatever order the wire uses.
    /// </summary>
    /// <param name="interleavedVolts">Frames × <paramref name="channelCount"/> samples. Valid only for the duration of the call.</param>
    /// <param name="channelCount">Channels per frame, 2 on every device here.</param>
    /// <param name="sampleRateHz">The stream's sample rate.</param>
    /// <param name="startFrameIndex">The stream-relative index of the first frame, so a recording can say where a buffer it never received would have gone.</param>
    void WriteStimulus(ReadOnlySpan<float> interleavedVolts, int channelCount, double sampleRateHz, long startFrameIndex);
}
