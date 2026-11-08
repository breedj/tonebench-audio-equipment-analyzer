namespace Tonebench.Device.Hardware;

/// <summary>
/// One member of the QA40x analyzer family: its USB product id, the name it reports, and the few
/// parameters that differ between models. Everything else about the protocol is shared.
///
/// <para><b>Why a record of data rather than a subclass.</b> <see cref="Qa40xRegisters"/> is
/// <c>internal static</c> and <see cref="Qa40xCalibration"/> is <c>internal sealed</c> with a private
/// constructor, so neither can be specialised — and neither should be, because what varies between
/// these two models is a handful of values, not behaviour. qa40x-rs-main reached the same conclusion
/// in its own words (types.rs:11-18): "The USB protocol is shared across the family; only a few
/// parameters differ (PID, available sample rates, firmware-flash support), so one device
/// implementation is parametrised by this."</para>
///
/// <para><b>What the sources establish.</b> Four independent trees treat the QA402 and the QA403 as
/// one protocol:</para>
/// <list type="bullet">
/// <item><description><c>reference/qa40x-baremetal/QA40x_BareMetal/Form1.cs:29-30</c> and <c>:42-43</c>
/// — a "Connect to QA402" button beside the QA403 one, both calling the same
/// <c>Usb.Open(int vid, int pid)</c>. Nothing else in that project is model-specific.</description></item>
/// <item><description><c>reference/PyQa40x-main/.../analyzer.py:56-58</c> — QuantAsylum's own library
/// opens the QA402 pid, falls back to the QA403 pid, and then runs a single code path: interface 0,
/// the same EP1/EP2 pairs, the same registers, the same 512-byte calibration page.</description></item>
/// <item><description><c>reference/ASIO40x-.../src/asio401/ASIO401/qa403.h:13-14</c> — "Despite the
/// name, this code also work with the QA402 since the protocol is supposed to be identical."
/// Its dispatch (<c>asio401.cpp:279-282</c>) constructs a QA402 <i>as the QA403 class</i>, while the
/// QA401 — a genuinely different device — gets a class of its own.</description></item>
/// <item><description><c>reference/qa40x-rs-main/.../qa40x/types.rs:11-108</c> — a model enum whose
/// only consequences are the sample-rate table and a firmware-flash gate.</description></item>
/// </list>
///
/// <para><b>What they do not establish.</b> No QA402 has ever been attached to this bench, so not one
/// figure here is measured the way the QA403's are. Three differences are named by the sources and
/// are honoured below (product id; 384 kHz being QA403-only, docs/protocol.md §2.4; and
/// <c>0x1C CAPABILITY2</c>, a model-dependent constant this app never reads). A fourth runs the other
/// way and is also not read: <c>0x13 TELEM_ISO_CURRENT</c> is a real sensor on the QA402 the
/// reference capture came from and answers a constant on this QA403 — see
/// <see cref="Qa40xRegisters.DecodeTelemetry"/>'s own remarks. A fifth was measured on a real QA402
/// on 2026-11-07 and is the one place this family is now known <i>not</i> to be uniform: the ADC's
/// channel order, <see cref="AdcChannelsSwapped"/>. Everything else — the two range ladders, the
/// calibration page layout, the wire format, the DAC's own L/R swap — is shared only insofar as
/// dual-model code that never branches can establish it. The one third-party statement about the
/// calibration page is <c>reference/ASIO40x-.../calibration.h:13</c>: "Factory calibration data
/// stored in the QA402/QA403 flash."</para>
/// </summary>
/// <param name="ProductId">USB product id, the one value that identifies the model on the bus.</param>
/// <param name="ProductName">What <see cref="DeviceInfo.ProductName"/> reports, and so what the
/// status bar's pill prints.</param>
/// <param name="Verified">
/// Whether this model has been exercised against real hardware by this project. False for the QA402,
/// and that is surfaced rather than buried: <c>DeviceStatusResponse.ModelUnverified</c> carries it to
/// the status bar and <c>ConnectAsync</c> logs it at Warning. A measurement is only as trustworthy as
/// the claim that the device was driven correctly, and for the QA402 that claim rests on documents.
/// </param>
/// <param name="AdcChannelsSwapped">
/// Whether the <b>ADC</b> frame puts the right channel in wire slot 0. False on the QA403, true on
/// the QA402 — the one difference between these two models found by measurement rather than read
/// out of a reference, and the only thing in this record that changes a sample value rather than a
/// capability.
///
/// <para>The <i>DAC</i>'s swap is unconditional and shared (<c>TransferPump.BuildTxChunk</c>): all
/// four references agree on it, and a DMM on a QA402's output BNCs confirmed it directly. What
/// differs is the way back. On the QA403, driving wire slot 1 alone returns on RX slot 0, so logical
/// left — which the TX swap puts on slot 1 — arrives on slot 0 and needs no second swap. On the
/// QA402 (serial 3738123988, 2026-11-07), a logical-right tone looped right-to-right read back as
/// <i>left</i>, so the physical right input lands in slot 0 and the slots must be exchanged.</para>
///
/// <para>That makes the QA403 the odd one out, which is what the references were disagreeing about
/// all along: PyQa40x and ASIO401 read the ADC straight through (right for a QA403, wrong for a
/// QA402) while qa40x-baremetal and qa40x-rs swap it (right for a QA402, wrong for a QA403), and
/// baremetal's comment claims "QA402, QA403, QA404" where only one of the three is its own case.
/// ASIO401 came closest: its input <c>swapChannels</c> is <c>true</c> for a QA401 and <c>false</c>
/// for a QA403, keyed on the device — it simply assumes a QA402 behaves like a QA403, which is the
/// assumption this field exists to correct.</para>
///
/// <para>So a QA404 is an open question rather than a safe guess, and so is a QA401, which this app
/// does not open at all.</para>
/// </param>
internal sealed record Qa40xModel(
    int ProductId,
    string ProductName,
    bool Verified,
    bool AdcChannelsSwapped)
{
    /// <summary>
    /// The QA403: the model this project was built against and the only one any measurement here was
    /// taken on.
    /// </summary>
    public static Qa40xModel Qa403 { get; } = new(
        Qa40xRegisters.Qa403ProductId, "QA403", Verified: true, AdcChannelsSwapped: false);

    /// <summary>
    /// The QA402, the QA403's discontinued predecessor. Same ladders, same registers, same
    /// calibration page, one fewer sample rate — and untested here.
    /// </summary>
    public static Qa40xModel Qa402 { get; } = new(
        Qa40xRegisters.Qa402ProductId, "QA402", Verified: false, AdcChannelsSwapped: true);

    /// <summary>
    /// Every model this app will open, <b>in the order the transport tries them</b>.
    ///
    /// <para>QA403 first, which is deliberately the opposite of PyQa40x's order (analyzer.py:56 tries
    /// the QA402 first). With one analyzer attached the order cannot matter; with both attached this
    /// is a QA403 bench, and the verified model should win. The list is also the single place a third
    /// model would be added — <c>UdevRulesTests</c> reads it and fails if the Linux access rule does
    /// not cover every entry.</para>
    /// </summary>
    public static IReadOnlyList<Qa40xModel> All { get; } = [Qa403, Qa402];

    /// <summary>The model with this product id, or null for an id this app does not know.</summary>
    public static Qa40xModel? TryFromProductId(int productId)
        => All.FirstOrDefault(m => m.ProductId == productId);

    /// <summary>
    /// What this model can do. <paramref name="allow384kHz"/> is the experimental setting, and it is
    /// <b>ignored on any model but the QA403</b>: register 9 index 3 is documented as QA403-only
    /// (docs/protocol.md §2.4), so offering it elsewhere would invite a write this family's own
    /// reference trees do not support — against hardware nobody here has ever run. The flag arrives
    /// as a value read live rather than latched, for the reason <c>Qa40xUsbDevice.Allow384kHz</c>
    /// gives.
    /// </summary>
    public DeviceCapabilities Capabilities(bool allow384kHz) => ProductId == Qa403.ProductId
        ? (allow384kHz ? DeviceCapabilities.Qa403Experimental : DeviceCapabilities.Qa403)
        : DeviceCapabilities.Qa402;

    /// <summary>
    /// The highest rate a <i>voiced</i> generator may be configured at — 192 kHz on both models, for
    /// two different reasons, which is why it is not simply the top of
    /// <see cref="Capabilities"/>.<c>SampleRatesHz</c>.
    ///
    /// <para>On the QA403 it is a hardware-protection rule: the ADC runs at 384 kHz and the DAC
    /// produces garbage there, including near-DC content that cycled the output protection relay
    /// until the device was power-cycled (<see cref="DeviceCapabilities.Qa403Experimental"/>). On the
    /// QA402 192 kHz is simply the top of the device — "QA402 24-bit/192 KSPS Audio Analyzer", and
    /// index 3 is QA403-only — so here the ceiling and the rate list coincide. Both are 192 000, and
    /// a later change to one must not silently move the other.</para>
    /// </summary>
    public double MaxGeneratorSampleRateHz => 192_000.0;
}
