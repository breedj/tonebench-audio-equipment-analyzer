namespace Tonebench.Device;

/// <summary>
/// Static capabilities of a connected device: supported sample rates and available input/
/// output gain ranges. Reported once after <see cref="IAnalyzerDevice.ConnectAsync"/>.
/// </summary>
public sealed record DeviceCapabilities(
    IReadOnlyList<double> SampleRatesHz,
    IReadOnlyList<double> InputRangesDbv,
    IReadOnlyList<double> OutputRangesDbv,
    int MaxChannelCount)
{
    /// <summary>
    /// The real QA403's ranges, per <c>docs/protocol.md</c> (register 5 = input range, 0..7 ->
    /// {0,6,...,42} dBV in 6 dB steps; register 6 = output range, 0..3 -> {-12,-2,8,18} dBV).
    /// Sample rates are register 9's index table minus 384 kHz — see
    /// <see cref="Qa403Experimental"/> for why that one is not here.
    /// </summary>
    public static DeviceCapabilities Qa403 { get; } = new(
        SampleRatesHz: [48_000, 96_000, 192_000],
        InputRangesDbv: [0, 6, 12, 18, 24, 30, 36, 42],
        OutputRangesDbv: [-12, -2, 8, 18],
        MaxChannelCount: 2);

    /// <summary>
    /// <see cref="Qa403"/> plus 384 kHz, reported only while the experimental setting is on — see
    /// <c>Qa40xUsbDevice.Allow384kHz</c>.
    ///
    /// <para><b>384 kHz is input-only, and that is measured, not inherited from a datasheet.</b>
    /// Register 9 index 3 is well sourced (docs/protocol.md §2.4) and the ADC really does re-clock:
    /// measured here on 2026-10-03, serial 4039_C589, frames arrived at 382 743 Hz against 384 000
    /// requested (99.67 %, with the 192 kHz control leg at 99.33 %), and the front end's own
    /// 1-20 kHz noise read within <b>0.05 dB</b> of the 192 kHz figure — so the samples are that
    /// converter running faster, not zero-stuffed or repeated.</para>
    ///
    /// <para>The DAC is a different story, exactly as <c>reference/ASIO40x-.../FAQ.md</c> warns
    /// ("only for input (recording) … if you try to use the outputs at 384 kHz, you will get
    /// garbage. This is a hardware limitation"). Measured in loopback against a 192 kHz leg on the
    /// same cable: a −6 dBV tone came back at <b>−18.69 dBV</b> with THD <b>−7.1 dBc</b> (~44 %),
    /// against −6.58 dBV and −106.9 dBc at 192 kHz. It is not a half-rate DAC — the tone is still at
    /// 999 Hz and f/2 sits 42 dB down — it is broadband mangling, with a <b>−14.9 dBc component at
    /// 3 Hz</b> where 192 kHz has −47 dBc. That near-DC content tripped the QA403's own output
    /// protection relay into cycling about once a second, and <b>a StreamingStop did not clear it;
    /// the device needed a power cycle.</b> So refusing the generator here
    /// (<see cref="IAnalyzerDevice.MaxGeneratorSampleRateHz"/>) protects the hardware, and is not a
    /// quality judgement that a caller may reasonably override.</para>
    ///
    /// <para>Note also what 384 kHz does <i>not</i> buy, measured on the same bench: the analog
    /// filters do not move with the rate. The loopback was ~32 dB down at 120 kHz and ~51 dB at
    /// 180 kHz, so there is no usable band above what 192 kHz already reaches. That figure is
    /// TX-and-RX in series through the broken DAC, so the <i>input</i> path's own bandwidth above
    /// 96 kHz remains unverified — settling it needs an external signal source, which this bench
    /// does not have.</para>
    /// </summary>
    public static DeviceCapabilities Qa403Experimental { get; } =
        Qa403 with { SampleRatesHz = [48_000, 96_000, 192_000, 384_000] };

    /// <summary>
    /// The QA402, the QA403's discontinued predecessor. <b>Identical to <see cref="Qa403"/> in every
    /// field</b>, and written out anyway for two reasons: so the differences have a documented home,
    /// and so a divergence found later has somewhere to go that is not a conditional buried in the
    /// device. qa40x-rs-main keeps its own per-model table for the same reason and says so
    /// (types.rs:96-97: "Both models expose the same four output ranges as far as we know; if a unit
    /// ever differs, give it its own list here").
    ///
    /// <para><b>The sample rates are where this is not merely a copy.</b> There is deliberately no
    /// <c>Qa402Experimental</c>: register 9 index 3 is documented as QA403-only (docs/protocol.md
    /// §2.4), so the experimental 384 kHz setting must not reach a QA402 at all — see
    /// <see cref="Qa40xModel.Capabilities"/>, which ignores the flag for this model. The QA402's own
    /// brief calls it a "24-bit/192 KSPS Audio Analyzer", so 192 kHz is the top of the device rather
    /// than a restriction this app imposes.</para>
    ///
    /// <para>The two ladders are asserted equal to the QA403's by <c>Qa40xModelTests</c> rather than
    /// written as a <c>with</c>-derivation, so that making them differ is a deliberate act that
    /// fails a test and forces a citation — which is the thing this family has no measurement for.
    /// Their shared values come from code that runs on both models without branching
    /// (baremetal Form1.cs:70-94, PyQa40x control.py:16-17), and from
    /// <c>reference/ASIO40x-.../qa403.h:16-24</c>, whose QA403 class — used for the QA402 too —
    /// enumerates the same eight input steps.</para>
    ///
    /// <para><b>Nothing here is measured.</b> No QA402 has been attached to this bench; see
    /// <see cref="Qa40xModel"/> for what the four reference trees do and do not establish.</para>
    /// </summary>
    public static DeviceCapabilities Qa402 { get; } = new(
        SampleRatesHz: [48_000, 96_000, 192_000],
        InputRangesDbv: [0, 6, 12, 18, 24, 30, 36, 42],
        OutputRangesDbv: [-12, -2, 8, 18],
        MaxChannelCount: 2);
}
