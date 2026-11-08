using Tonebench.Device.Hardware;

namespace Tonebench.Device.Tests.Hardware;

/// <summary>
/// The QA40x family table. These are the tests that matter for a model nobody here can plug in: the
/// QA402's protocol is reasoned from documents (see <see cref="Qa40xModel"/> for the four sources),
/// so what can still be pinned is that this app does not offer it anything the sources do not.
/// </summary>
public class Qa40xModelTests
{
    [Fact]
    public void ProductIds_AreTheOnesTheReferenceTreesUse()
    {
        // reference/qa40x-baremetal/QA40x_BareMetal/Form1.cs:30 and :43 — the two Connect buttons.
        Assert.Equal(0x4e39, Qa40xModel.Qa403.ProductId);
        Assert.Equal(0x4e37, Qa40xModel.Qa402.ProductId);
        Assert.Equal(0x16c0, Qa40xRegisters.Qa40xVendorId);
    }

    [Fact]
    public void TryFromProductId_MapsEachKnownIdAndRefusesAnythingElse()
    {
        Assert.Same(Qa40xModel.Qa403, Qa40xModel.TryFromProductId(0x4e39));
        Assert.Same(Qa40xModel.Qa402, Qa40xModel.TryFromProductId(0x4e37));

        // 0x4e35 is the QA451 programmable load, which sits on the same shared VID and is emphatically
        // not an analyzer — Qa40xTransport must never claim its interface.
        Assert.Null(Qa40xModel.TryFromProductId(Qa451Protocol.Qa451ProductId));
        Assert.Null(Qa40xModel.TryFromProductId(0x0000));
    }

    /// <summary>
    /// The QA403 leads <see cref="Qa40xModel.All"/>, which is what the transport's probe order
    /// follows. Deliberately the opposite of PyQa40x's (analyzer.py:56 tries the QA402 first): with
    /// one analyzer attached the order cannot matter, and with both attached the verified model
    /// should win on a bench that owns one.
    /// </summary>
    [Fact]
    public void All_TriesTheVerifiedModelFirst_AndListsEveryModelOnce()
    {
        Assert.Equal([Qa40xModel.Qa403, Qa40xModel.Qa402], Qa40xModel.All);
        Assert.True(Qa40xModel.All[0].Verified);
        Assert.Distinct(Qa40xModel.All.Select(m => m.ProductId));
        Assert.Distinct(Qa40xModel.All.Select(m => m.ProductName));
    }

    /// <summary>
    /// <b>The test this file exists for.</b> Register 9 index 3 is documented as QA403-only
    /// (docs/protocol.md §2.4), so the experimental 384 kHz setting must not reach a QA402 however it
    /// is set — and the setting is a global checkbox, so a QA402 on a bench whose owner once ticked it
    /// is the ordinary case rather than a corner.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheQa402_NeverReports384kHz_WhateverTheExperimentalSettingSays(bool allow384kHz)
    {
        var rates = Qa40xModel.Qa402.Capabilities(allow384kHz).SampleRatesHz;

        Assert.Equal([48_000, 96_000, 192_000], rates);
        Assert.DoesNotContain(384_000, rates);
    }

    /// <summary>The other half: the QA403 still gains the rate, or the gate above has broken it.</summary>
    [Fact]
    public void TheQa403_Reports384kHz_OnlyWhenTheExperimentalSettingIsOn()
    {
        Assert.DoesNotContain(384_000, Qa40xModel.Qa403.Capabilities(allow384kHz: false).SampleRatesHz);
        Assert.Contains(384_000, Qa40xModel.Qa403.Capabilities(allow384kHz: true).SampleRatesHz);
    }

    /// <summary>
    /// The two ladders are asserted equal to each other rather than against hand-written lists, so
    /// making them differ is a deliberate act that fails here and forces a citation — which is
    /// precisely what this family has no measurement for. Their shared values come from code that
    /// runs on both models without branching (baremetal Form1.cs:70-94, PyQa40x control.py:16-17)
    /// and from reference/ASIO40x-.../qa403.h, whose QA403 class serves the QA402 too.
    /// </summary>
    [Fact]
    public void BothModels_ShareTheRangeLaddersAndChannelCount()
    {
        var qa402 = Qa40xModel.Qa402.Capabilities(allow384kHz: false);
        var qa403 = Qa40xModel.Qa403.Capabilities(allow384kHz: false);

        Assert.Equal(qa403.InputRangesDbv, qa402.InputRangesDbv);
        Assert.Equal(qa403.OutputRangesDbv, qa402.OutputRangesDbv);
        Assert.Equal(qa403.MaxChannelCount, qa402.MaxChannelCount);

        // And the ladders are the device's own, not something this table invented.
        Assert.Equal(Qa40xRegisters.InputRangesDbv, qa402.InputRangesDbv);
        Assert.Equal(Qa40xRegisters.OutputRangesDbv, qa402.OutputRangesDbv);
    }

    /// <summary>
    /// Every rate either model reports has to be encodable, or the capability list promises something
    /// the register table cannot express — which would surface as an exception mid-acquisition rather
    /// than as a refusal up front.
    /// </summary>
    [Fact]
    public void EveryReportedRate_IsOneTheRegisterTableCanEncode()
    {
        foreach (var model in Qa40xModel.All)
        {
            foreach (var allow384kHz in (bool[])[false, true])
            {
                foreach (var rate in model.Capabilities(allow384kHz).SampleRatesHz)
                {
                    // Throws if the table does not know the rate; the assert pins the QA403-only index.
                    uint index = Qa40xRegisters.SampleRateHzToRegister(rate);
                    Assert.True(index <= 3);
                }
            }
        }
    }

    /// <summary>
    /// 192 kHz on both, and the equality is load-bearing in opposite directions: on the QA403 it is a
    /// hardware-protection rule (its DAC mangles 384 kHz and cycled the output protection relay), on
    /// the QA402 it is simply the top of the device. A change to one must not silently move the other.
    /// </summary>
    [Fact]
    public void TheGeneratorCeiling_Is192kHzOnBothModels()
    {
        Assert.Equal(192_000.0, Qa40xModel.Qa403.MaxGeneratorSampleRateHz);
        Assert.Equal(192_000.0, Qa40xModel.Qa402.MaxGeneratorSampleRateHz);

        // On the QA402 the ceiling and the rate list coincide; on the QA403 under the experimental
        // setting they deliberately do not, which is the whole point of the generator refusal.
        Assert.Equal(192_000.0, Qa40xModel.Qa402.Capabilities(allow384kHz: true).SampleRatesHz.Max());
        Assert.Equal(384_000.0, Qa40xModel.Qa403.Capabilities(allow384kHz: true).SampleRatesHz.Max());
    }

    /// <summary>
    /// Only the QA403 is verified, and that flag is what puts the warning in the log at connect —
    /// nothing on screen any more. Pinned so that marking the QA402 verified is a deliberate edit
    /// made when somebody has actually run one.
    /// </summary>
    [Fact]
    public void OnlyTheQa403_IsMarkedVerified()
    {
        Assert.True(Qa40xModel.Qa403.Verified);
        Assert.False(Qa40xModel.Qa402.Verified);
    }

    /// <summary>
    /// The ADC's channel order, which is the one thing these two models are known to do differently
    /// and the only field here that changes a sample rather than a capability.
    ///
    /// <para>Both values are measured. The QA403's comes from this bench (driving wire slot 1 alone
    /// returns on RX slot 0, so the TX swap already puts logical left where the reader expects it);
    /// the QA402's from serial 3738123988 on 2026-11-07, where a logical-right tone looped
    /// right-to-right read back as left. Pinned in both directions because getting either wrong
    /// crosses every stereo measurement on that model while leaving each channel's own figures
    /// perfectly plausible — there is nothing in a THD or a level reading to say it happened.</para>
    ///
    /// <para>The QA403 case is the regression that would cost most: it is the verified model, and a
    /// well-meaning "the family is uniform" simplification is exactly what would break it.</para>
    /// </summary>
    [Fact]
    public void OnlyTheQa402_SwapsItsAdcChannels()
    {
        Assert.False(Qa40xModel.Qa403.AdcChannelsSwapped);
        Assert.True(Qa40xModel.Qa402.AdcChannelsSwapped);
    }
}
