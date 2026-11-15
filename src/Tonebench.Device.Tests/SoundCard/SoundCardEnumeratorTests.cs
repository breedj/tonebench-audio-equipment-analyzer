using Tonebench.Device.SoundCard;

namespace Tonebench.Device.Tests.SoundCard;

/// <summary>
/// The enumerator's rules, all of which came out of measuring a real machine rather than from the
/// PortAudio documentation.
/// </summary>
public class SoundCardEnumeratorTests
{
    private static SoundCardDeviceDescriptor Device(
        string name, int inputs = 2, int outputs = 0, int hostApi = 0,
        SoundCardHostApiKind kind = SoundCardHostApiKind.Wasapi, int index = 0) =>
        new(index, name, hostApi, "Windows WASAPI", kind, inputs, outputs, 0.01, 0.01, 48_000.0);

    // ---- the interface stem --------------------------------------------------------------------

    [Theory]
    [InlineData("In 1-2 (MOTU M Series)", "MOTU M Series")]
    [InlineData("Out 1-2 (MOTU M Series)", "MOTU M Series")]
    // Real device names nest parentheses; taking the last pair would read "R) Audio" here.
    [InlineData("Speaker (Realtek(R) Audio)", "Realtek(R) Audio")]
    [InlineData("Microfoonmatrix (Intel(R) Smart Sound)", "Intel(R) Smart Sound")]
    public void InterfaceStem_IsWhatIsInsideTheLastParentheses(string deviceName, string expected) =>
        Assert.Equal(expected, SoundCardEnumerator.InterfaceStem(deviceName));

    /// <summary>
    /// WDM-KS repeats the endpoint instead of naming the card. Read as a stem it would make every
    /// WDM-KS input match only itself, so it has to come back empty — which is what turns the
    /// pairing advisory rather than a false negative.
    /// </summary>
    [Theory]
    [InlineData("In 1-2 (In 1-2)")]
    [InlineData("Out 1-2 (Out 1-2)")]
    [InlineData("Microfoonmatrix 1 ()")]
    [InlineData("Primair geluidsstuurprogramma")]
    public void InterfaceStem_WhenTheNameNamesNoInterface_IsEmpty(string deviceName) =>
        Assert.Equal(string.Empty, SoundCardEnumerator.InterfaceStem(deviceName));

    // ---- render loopback -----------------------------------------------------------------------

    /// <summary>
    /// A WASAPI render loopback is not a recording device, and nothing but its name says so.
    ///
    /// <para>Windows offers one beside every playback endpoint and PortAudio lists it with input
    /// channels and no output channels, which is exactly an ADC's shape -- so the picker offered a
    /// TOPPING D50 III, a DAC with no inputs at all, as an input card (reported 2026-11-13). What
    /// it captures is the stream being rendered to that endpoint, after the mixer and before the
    /// converter, so it cannot hear what the analyzer plays.</para>
    /// </summary>
    [Theory]
    [InlineData("Luidsprekers (TOPPING USB DAC) [Loopback]", true)]
    [InlineData("Speakers (Realtek(R) Audio) [Loopback]", true)]
    // The case PortAudio does not produce, checked because the comparison ignores case.
    [InlineData("Speakers [loopback]", true)]
    [InlineData("In 1-2 (MOTU M Series)", false)]
    // A card that merely has the word in its own name is a real input, and the suffix is a suffix.
    [InlineData("Loopback In 1-2 (Some Card)", false)]
    [InlineData("", false)]
    public void IsRenderLoopback_ReadsTheSuffixPortAudioAppends(string deviceName, bool expected) =>
        Assert.Equal(expected, SoundCardEnumerator.IsRenderLoopback(deviceName));

    [Fact]
    public void IsRenderLoopback_OfNothing_IsFalse() =>
        Assert.False(SoundCardEnumerator.IsRenderLoopback(null));

    // ---- pairing -------------------------------------------------------------------------------

    [Fact]
    public void Pairing_OfTwoEndpointsOfOneNamedInterface_MatchesByName()
    {
        var input = Device("In 1-2 (MOTU M Series)", inputs: 2, outputs: 0, index: 0);
        var output = Device("Out 1-2 (MOTU M Series)", inputs: 0, outputs: 2, index: 1);

        Assert.Equal(SoundCardPairing.NamesMatch, SoundCardEnumerator.Pairing(input, output));
    }

    /// <summary>What ASIO gives, and the only case with nothing to check.</summary>
    [Fact]
    public void Pairing_OfOneFullDuplexDeviceWithItself_NeedsNoNameCheck()
    {
        var duplex = Device("MOTU M Series ASIO", inputs: 4, outputs: 4, kind: SoundCardHostApiKind.Asio, index: 3);

        Assert.Equal(SoundCardPairing.FullDuplexDevice, SoundCardEnumerator.Pairing(duplex, duplex));
    }

    /// <summary>
    /// The WDM-KS case. It must come back Unknown — warned about, never refused — because on
    /// hardware where WDM-KS is the best host API available, blocking here would leave nothing.
    /// </summary>
    [Fact]
    public void Pairing_UnderWdmKs_IsUnknownRatherThanRefused()
    {
        var input = Device("In 1-2 (In 1-2)", inputs: 2, outputs: 0, hostApi: 1, kind: SoundCardHostApiKind.WdmKs, index: 0);
        var output = Device("Out 1-2 (Out 1-2)", inputs: 0, outputs: 2, hostApi: 1, kind: SoundCardHostApiKind.WdmKs, index: 1);

        Assert.Equal(SoundCardPairing.Unknown, SoundCardEnumerator.Pairing(input, output));
    }

    [Fact]
    public void Pairing_AcrossTwoHostApis_IsUnknown()
    {
        var input = Device("In 1-2 (MOTU M Series)", inputs: 2, outputs: 0, hostApi: 0, index: 0);
        var output = Device("Out 1-2 (MOTU M Series)", inputs: 0, outputs: 2, hostApi: 1, index: 1);

        Assert.Equal(SoundCardPairing.Unknown, SoundCardEnumerator.Pairing(input, output));
    }

    /// <summary>
    /// <b>This asserted <c>Unknown</c> until 2026-11-15</b>, and the change is the point rather
    /// than a correction: two names that each say which card they belong to, and say different
    /// ones, are positive evidence of two interfaces, where <c>Unknown</c> is the absence of
    /// evidence either way. Telling those apart is what lets a sound-card bench be narrowed to one
    /// interface without making every WDM-KS pairing unopenable — see
    /// <see cref="Pairing_UnderWdmKs_IsUnknownRatherThanRefused"/>, which still passes unchanged.
    ///
    /// <para>Note the Realtek name is also the <c>InterfaceStem</c> nesting case
    /// (<c>Speaker (Realtek(R) Audio)</c>), so a stem taken from the <i>last</i> <c>(</c> would
    /// read the trademark as the interface and this test would not notice.</para>
    /// </summary>
    [Fact]
    public void Pairing_OfTwoDifferentCards_IsDifferentInterfaces()
    {
        var input = Device("In 1-2 (MOTU M Series)", inputs: 2, outputs: 0, index: 0);
        var output = Device("Speaker (Realtek(R) Audio)", inputs: 0, outputs: 2, index: 1);

        Assert.Equal(SoundCardPairing.DifferentInterfaces, SoundCardEnumerator.Pairing(input, output));
    }

    /// <summary>
    /// Two cards enumerated under <i>different</i> host APIs are still two cards, which is the
    /// pairing that matters most here: a two-card bench is exactly where the better converter keeps
    /// ASIO and the other takes WASAPI. Answering Unknown merely because the APIs differ would
    /// throw the evidence away in the one case it is needed, which is why the stem comparison runs
    /// before the host-API test.
    /// </summary>
    [Fact]
    public void Pairing_OfTwoDifferentCardsAcrossHostApis_StillSeesTwoCards()
    {
        var input = Device("In 1-2 (MOTU M Series)", inputs: 2, outputs: 0, hostApi: 0, index: 0);
        var output = Device(
            "Luidsprekers (TOPPING USB DAC)", inputs: 0, outputs: 2,
            hostApi: 1, kind: SoundCardHostApiKind.WdmKs, index: 1);

        Assert.Equal(SoundCardPairing.DifferentInterfaces, SoundCardEnumerator.Pairing(input, output));
    }

    // ---- one interface -------------------------------------------------------------------------

    /// <summary>
    /// The narrowing: a sound-card bench is one physical interface, and two named cards are turned
    /// away with the analyzer kind that does measure them named in the message.
    /// </summary>
    [Fact]
    public void OneInterfaceProblem_OfTwoNamedCards_RefusesAndNamesSplit()
    {
        var input = Device("In 1-2 (MOTU M Series)", inputs: 2, outputs: 0, index: 0);
        var output = Device("Luidsprekers (TOPPING USB DAC)", inputs: 0, outputs: 2, index: 1);

        string problem = Assert.IsType<string>(SoundCardEnumerator.OneInterfaceProblem(input, output));

        Assert.Contains("MOTU M Series", problem);
        Assert.Contains("TOPPING USB DAC", problem);
        Assert.Contains("Split", problem);
    }

    /// <summary>
    /// The regression that would hurt most: the ordinary one-card bench, whose two endpoint names
    /// <i>differ</i> (<c>In 1-2</c> against <c>Out 1-2</c>) while naming one interface. A rule
    /// written on whole names rather than on the interface inside them refuses this — which is
    /// every MOTU, Focusrite and Scarlett bench there is.
    /// </summary>
    [Fact]
    public void OneInterfaceProblem_OfOneCardsTwoEndpoints_IsNothing()
    {
        var input = Device("In 1-2 (MOTU M Series)", inputs: 2, outputs: 0, index: 0);
        var output = Device("Out 1-2 (MOTU M Series)", inputs: 0, outputs: 2, index: 1);

        Assert.Null(SoundCardEnumerator.OneInterfaceProblem(input, output));
    }

    /// <summary>
    /// Positive evidence only. A WDM-KS pairing says nothing about which card either end is, and it
    /// opens exactly as it did — it keeps the drift warning it always had, which is the honest
    /// answer to "we cannot tell".
    /// </summary>
    [Fact]
    public void OneInterfaceProblem_WhenTheNamesSayNothing_IsNothing()
    {
        var input = Device("In 1-2 (In 1-2)", inputs: 2, outputs: 0, hostApi: 1, kind: SoundCardHostApiKind.WdmKs, index: 0);
        var output = Device("Out 1-2 (Out 1-2)", inputs: 0, outputs: 2, hostApi: 1, kind: SoundCardHostApiKind.WdmKs, index: 1);

        Assert.Null(SoundCardEnumerator.OneInterfaceProblem(input, output));
    }

    /// <summary>
    /// A one-direction bench has no pairing to doubt, and this is also every half of a split, whose
    /// settings are masked to a single direction — so the narrowing must say nothing about it or it
    /// would refuse the two-card bench it is meant to send there.
    /// </summary>
    [Fact]
    public void OneInterfaceProblem_OfOneDirectionAlone_IsNothing()
    {
        var input = Device("In 1-2 (MOTU M Series)", inputs: 2, outputs: 0, index: 0);
        var output = Device("Luidsprekers (TOPPING USB DAC)", inputs: 0, outputs: 2, index: 1);

        Assert.Null(SoundCardEnumerator.OneInterfaceProblem(input, output: null));
        Assert.Null(SoundCardEnumerator.OneInterfaceProblem(input: null, output));
    }

    // ---- rates ---------------------------------------------------------------------------------

    /// <summary>
    /// Reproduces the measured WASAPI difference: shared offers only the Windows mixer's 48 kHz,
    /// exclusive offers everything the converter can do. A rate list taken in shared mode would
    /// quietly cap this app at 48 kHz.
    /// </summary>
    [Fact]
    public void SupportedRates_UnderWasapi_DifferBetweenSharedAndExclusive()
    {
        var fake = new FakePortAudio();
        var api = fake.AddHostApi("Windows WASAPI", SoundCardHostApiKind.Wasapi);
        var input = fake.AddDevice(api, "In 1-2 (MOTU M Series)", inputs: 2, outputs: 0);
        var output = fake.AddDevice(api, "Out 1-2 (MOTU M Series)", inputs: 0, outputs: 2);
        fake.FormatSupported = r => r.Exclusive || r.SampleRateHz == 48_000.0;

        var enumerator = new SoundCardEnumerator(fake);

        Assert.Equal([48_000.0], enumerator.SupportedRatesHz(input, output, exclusive: false));

        // Everything asked about, derived rather than listed: this fake accepts every rate in
        // exclusive mode, so the expectation is the candidate list itself and does not have to be
        // rewritten the next time one is added -- which is what broke it when 384 and 768 arrived.
        Assert.Equal(
            SoundCardEnumerator.CandidateRatesHz,
            enumerator.SupportedRatesHz(input, output, exclusive: true));
    }

    /// <summary>
    /// Only the rates this app asks about. A card offering 32 kHz does not get to put it in front of
    /// a user: it is in neither rate family, so nothing here has reasoned about what a split pairing
    /// it against 48 or 44.1 kHz would do to a periodic stimulus.
    /// </summary>
    [Fact]
    public void SupportedRates_AreOnlyEverTheOnesTheAppAsksAbout()
    {
        var fake = new FakePortAudio();
        var api = fake.AddHostApi("Windows WASAPI", SoundCardHostApiKind.Wasapi);
        var input = fake.AddDevice(api, "In (Card)", inputs: 2, outputs: 0);
        var output = fake.AddDevice(api, "Out (Card)", inputs: 0, outputs: 2);

        var rates = new SoundCardEnumerator(fake).SupportedRatesHz(input, output, exclusive: true);

        Assert.Equal(SoundCardEnumerator.CandidateRatesHz, rates);
        Assert.DoesNotContain(32_000.0, rates);
        Assert.DoesNotContain(22_050.0, rates);
    }

    /// <summary>
    /// A one-direction card is probed <b>in that direction alone</b>, and passing it as both sides
    /// answers nothing instead — which is the shape every two-card bench uses, and the one the
    /// duplex probes above never covered.
    ///
    /// <para><b>Written after it bit.</b> A two-card hardware harness probed each output candidate
    /// as <c>SupportedRatesHz(card, card, exclusive: true)</c>, and this method reads two non-null
    /// sides as a duplex request — so a DAC with no capture channels reported no usable rate at
    /// all. On Windows only ASIO exposes a genuinely full-duplex device, so every WASAPI and WDM-KS
    /// output was excluded and the one thing that could still pass was an ASIO card. The symptom
    /// was not an error but a plausible one: <i>"no pair of cards at 192000 Hz"</i> on a bench that
    /// plainly had two, about a converter that in fact offered 44.1 kHz through 768 kHz in
    /// exclusive mode. It cost a hardware reboot before the probe itself was suspected.</para>
    ///
    /// <para>Both directions are asserted here rather than only the output one, because the two
    /// halves of a split are an input-only card and an output-only card and the mistake is
    /// available on either.</para>
    /// </summary>
    [Fact]
    public void SupportedRates_ForAOneDirectionCard_AreProbedInThatDirectionAlone()
    {
        var fake = new FakePortAudio();
        var api = fake.AddHostApi("Windows WASAPI", SoundCardHostApiKind.Wasapi);
        var adc = fake.AddDevice(api, "In 1-2 (MOTU M Series)", inputs: 2, outputs: 0);
        var dac = fake.AddDevice(api, "Luidsprekers (TOPPING USB DAC)", inputs: 0, outputs: 2);

        // What a driver really does: refuse a request asking for channels the device does not have,
        // whatever the rate. Neither card here carries both directions, so a duplex request fails
        // and a one-direction one succeeds -- keyed on the direction, which is what an absent side
        // decides.
        fake.FormatSupported = r => r.Direction != SoundCardStreamDirection.Duplex;

        var enumerator = new SoundCardEnumerator(fake);

        Assert.Equal(
            SoundCardEnumerator.CandidateRatesHz,
            enumerator.SupportedRatesHz(adc, null, exclusive: true));
        Assert.Equal(
            SoundCardEnumerator.CandidateRatesHz,
            enumerator.SupportedRatesHz(null, dac, exclusive: true));

        // The mistake, pinned from both ends: a card asked about itself is a duplex probe, and a
        // one-direction card answers it with nothing.
        Assert.Empty(enumerator.SupportedRatesHz(adc, adc, exclusive: true));
        Assert.Empty(enumerator.SupportedRatesHz(dac, dac, exclusive: true));
    }

    /// <summary>
    /// Every candidate belongs to one of the two families — a power-of-two multiple of 48 kHz or of
    /// 44.1 kHz — which is the rule and not a tidiness. A split refuses a generator/capture pair
    /// whose ratio is not a whole number, because a stimulus carrying its own period cannot line up
    /// with the analysis record otherwise, and within a family every pairing is a power-of-two ratio
    /// and so always can. A rate in neither family would be a third set of ratios nothing has
    /// reasoned about, so this test is what forces that decision rather than letting it arrive as a
    /// drifting ripple on somebody's trace.
    ///
    /// <para>It replaced a one-family version on 2026-11-14 when 44.1 kHz was added. Note what is
    /// deliberately <i>not</i> asserted: that the two families mix. They do not — see the
    /// cross-family case below.</para>
    /// </summary>
    [Fact]
    public void EveryCandidateRate_BelongsToOneOfTheTwoFamilies()
    {
        foreach (double rate in SoundCardEnumerator.CandidateRatesHz)
        {
            Assert.NotNull(SoundCardEnumerator.FamilyBaseHz(rate));
        }
    }

    [Theory]
    [InlineData(48_000.0, 48_000.0)]
    [InlineData(96_000.0, 48_000.0)]
    [InlineData(768_000.0, 48_000.0)]
    [InlineData(44_100.0, 44_100.0)]
    [InlineData(88_200.0, 44_100.0)]
    [InlineData(176_400.0, 44_100.0)]
    public void FamilyBase_IsTheBaseTheRateIsAPowerOfTwoMultipleOf(double rateHz, double expectedBaseHz) =>
        Assert.Equal(expectedBaseHz, SoundCardEnumerator.FamilyBaseHz(rateHz));

    [Theory]
    [InlineData(32_000.0)]   // a real card rate, in neither family
    [InlineData(22_050.0)]   // half of 44.1, so not a multiple of it
    [InlineData(24_000.0)]   // likewise for 48
    [InlineData(132_300.0)]  // 3 x 44.1: a whole multiple, but not a power-of-two one
    [InlineData(144_000.0)]  // 3 x 48, same
    public void FamilyBase_ForARateInNeitherFamily_IsNull(double rateHz) =>
        Assert.Null(SoundCardEnumerator.FamilyBaseHz(rateHz));

    /// <summary>
    /// The property the families exist for, asserted over the candidate list itself rather than
    /// over examples: two rates in one family are always an integer ratio, and two in different
    /// families never are. The second half is why a mixed pairing is permanently tone-only.
    /// </summary>
    [Fact]
    public void TwoRatesAreAnIntegerRatio_ExactlyWhenTheyShareAFamily()
    {
        double[] rates = SoundCardEnumerator.CandidateRatesHz;

        foreach (double a in rates)
        {
            foreach (double b in rates)
            {
                double ratio = Math.Max(a, b) / Math.Min(a, b);
                bool integerRatio = Math.Abs(ratio - Math.Round(ratio)) < 1e-9;
                bool sameFamily = SoundCardEnumerator.FamilyBaseHz(a) == SoundCardEnumerator.FamilyBaseHz(b);

                Assert.Equal(sameFamily, integerRatio);
            }
        }
    }

    [Fact]
    public void SupportedRates_OnACardThatAcceptsNoneOfThem_IsEmpty()
    {
        var fake = new FakePortAudio();
        var api = fake.AddHostApi("Windows WASAPI", SoundCardHostApiKind.Wasapi);
        var input = fake.AddDevice(api, "In (Card)", inputs: 2, outputs: 0);
        var output = fake.AddDevice(api, "Out (Card)", inputs: 0, outputs: 2);
        fake.FormatSupported = _ => false;

        Assert.Empty(new SoundCardEnumerator(fake).SupportedRatesHz(input, output, exclusive: true));
    }

    // ---- device lists and lookup ---------------------------------------------------------------

    /// <summary>
    /// The finding the whole pairing design rests on: on Windows a card is split, so a picker that
    /// listed only full-duplex devices would be empty.
    /// </summary>
    [Fact]
    public void OnTheMeasuredWindowsTopology_NoDeviceIsFullDuplex()
    {
        var enumerator = new SoundCardEnumerator(FakePortAudio.WithASplitInterface());

        Assert.NotEmpty(enumerator.Devices);
        Assert.DoesNotContain(enumerator.Devices, d => d.IsFullDuplex);
        Assert.NotEmpty(enumerator.InputDevices);
        Assert.NotEmpty(enumerator.OutputDevices);
    }

    /// <summary>
    /// Matched by name, because PortAudio's indices renumber whenever anything is plugged in — and
    /// an index that silently resolves to a different card is a calibration applied to the wrong
    /// hardware.
    /// </summary>
    [Fact]
    public void Find_MatchesByHostApiAndDeviceName()
    {
        var enumerator = new SoundCardEnumerator(FakePortAudio.WithASplitInterface());

        var found = enumerator.Find("Windows WASAPI", "In 3-4 (MOTU M Series)");

        Assert.NotNull(found);
        Assert.Equal("In 3-4 (MOTU M Series)", found.Name);
        Assert.Equal(SoundCardHostApiKind.Wasapi, found.HostApiKind);
    }

    [Fact]
    public void Find_WhenTheSavedCardIsGone_ReturnsNothingRatherThanTheWrongOne()
    {
        var enumerator = new SoundCardEnumerator(FakePortAudio.WithASplitInterface());

        Assert.Null(enumerator.Find("Windows WASAPI", "In 1-2 (Some Other Card)"));
    }

    /// <summary>The same device name under two host APIs is two different devices, and must not be confused.</summary>
    [Fact]
    public void Find_DistinguishesTheSameNameUnderDifferentHostApis()
    {
        var fake = new FakePortAudio();
        var wasapi = fake.AddHostApi("Windows WASAPI", SoundCardHostApiKind.Wasapi);
        var wdmks = fake.AddHostApi("Windows WDM-KS", SoundCardHostApiKind.WdmKs);
        fake.AddDevice(wasapi, "In 1-2 (Card)", inputs: 2, outputs: 0);
        fake.AddDevice(wdmks, "In 1-2 (Card)", inputs: 2, outputs: 0);

        var enumerator = new SoundCardEnumerator(fake);

        Assert.Equal(SoundCardHostApiKind.Wasapi, enumerator.Find("Windows WASAPI", "In 1-2 (Card)")!.HostApiKind);
        Assert.Equal(SoundCardHostApiKind.WdmKs, enumerator.Find("Windows WDM-KS", "In 1-2 (Card)")!.HostApiKind);
    }

    // ---- trust and ordering --------------------------------------------------------------------

    /// <summary>
    /// DirectSound and MME accept any rate by resampling in software — measured, both claimed 48,
    /// 96 and 192 kHz on a card whose WASAPI shared mode admitted to only 48.
    /// </summary>
    [Fact]
    public void Trust_MarksTheResamplingHostApis()
    {
        Assert.Equal(SoundCardHostApiTrust.Resamples, SoundCardEnumerator.Trust(SoundCardHostApiKind.DirectSound));
        Assert.Equal(SoundCardHostApiTrust.Resamples, SoundCardEnumerator.Trust(SoundCardHostApiKind.Mme));
        Assert.Equal(SoundCardHostApiTrust.RequiresExclusive, SoundCardEnumerator.Trust(SoundCardHostApiKind.Wasapi));
        Assert.Equal(SoundCardHostApiTrust.Trusted, SoundCardEnumerator.Trust(SoundCardHostApiKind.Asio));
        Assert.Equal(SoundCardHostApiTrust.Trusted, SoundCardEnumerator.Trust(SoundCardHostApiKind.WdmKs));
    }

    [Fact]
    public void Preference_PutsTheHostApisThatReachTheHardwareFirst()
    {
        var ordered = new[]
        {
            SoundCardHostApiKind.Mme,
            SoundCardHostApiKind.Asio,
            SoundCardHostApiKind.DirectSound,
            SoundCardHostApiKind.Wasapi,
            SoundCardHostApiKind.WdmKs,
        }
        .OrderBy(SoundCardEnumerator.PreferenceOf)
        .ToArray();

        Assert.Equal(SoundCardHostApiKind.Asio, ordered[0]);
        Assert.Equal(SoundCardHostApiKind.Mme, ordered[^1]);
        Assert.True(
            SoundCardEnumerator.PreferenceOf(SoundCardHostApiKind.WdmKs)
            < SoundCardEnumerator.PreferenceOf(SoundCardHostApiKind.Wasapi));
    }

    // ---- the channel pair ----------------------------------------------------------------------

    private static SoundCardDeviceDescriptor Input(string name, int channels) => Device(name, inputs: channels);

    private static SoundCardDeviceDescriptor Output(string name, int channels) => Device(name, inputs: 0, outputs: channels);

    [Theory]
    [InlineData(0, "1/2")]
    [InlineData(2, "3/4")]
    [InlineData(6, "7/8")]
    public void PairLabel_IsHowTheDialogWritesIt(int offset, string expected) =>
        Assert.Equal(expected, SoundCardEnumerator.PairLabel(offset));

    [Fact]
    public void APairTheDevicesHave_IsNoProblem()
    {
        Assert.Null(SoundCardEnumerator.ChannelPairProblem(
            Input("MOTU M Series", 8), 2, Output("MOTU M Series", 4), 2));
    }

    /// <summary>
    /// A direction this bench does not carry has no channels to fall short — which is not the same
    /// as a device that has none. A capture-only ADC and a playback-only DAC are each half of this,
    /// and complaining about the absent side would refuse a bench the device layer opens perfectly
    /// well.
    /// </summary>
    [Fact]
    public void AnAbsentDirection_HasNoChannelProblem()
    {
        Assert.Null(SoundCardEnumerator.ChannelPairProblem(
            Input("Cosmos ADC", 2), 0, output: null, 0));

        Assert.Null(SoundCardEnumerator.ChannelPairProblem(
            input: null, 0, Output("D50 III", 2), 0));
    }

    /// <summary>The side that *is* there is still checked, or the absent one would excuse it.</summary>
    [Fact]
    public void AnAbsentDirection_DoesNotExcuseThePairTheOtherSideCannotHonour()
    {
        string? problem = SoundCardEnumerator.ChannelPairProblem(
            Input("Cosmos ADC", 2), inputChannelOffset: 2, output: null, 0);

        Assert.NotNull(problem);
        Assert.Contains("Cosmos ADC", problem);
    }

    /// <summary>
    /// The message has to name the pair, the count it needs and the count the device has. The one
    /// it replaced -- "That channel pair is past what the card has." -- named none of the three,
    /// and the control that would answer it is usually not on screen: the dialog shows a
    /// channel-pair dropdown only where there is more than one pair to choose, so a two-channel
    /// card displays this beside nothing that mentions channels.
    /// </summary>
    [Fact]
    public void AnInputPairPastTheDevice_NamesThePairAndBothCounts()
    {
        string? problem = SoundCardEnumerator.ChannelPairProblem(
            Input("Microphone (Realtek(R) Audio)", 2), 2, Output("Speakers", 2), 0);

        Assert.Equal(
            "Channels 3/4 need 4 input channels and 'Microphone (Realtek(R) Audio)' has 2.",
            problem);
    }

    [Fact]
    public void AnOutputPairPastTheDevice_SaysOutput()
    {
        string? problem = SoundCardEnumerator.ChannelPairProblem(
            Input("In", 8), 0, Output("Speakers", 2), 4);

        Assert.Equal("Channels 5/6 need 6 output channels and 'Speakers' has 2.", problem);
    }

    /// <summary>
    /// Both sides at once, rather than whichever was tested first: a pair is chosen per direction,
    /// and fixing the input only to be told about the output is two trips through one dead end.
    /// </summary>
    [Fact]
    public void WhenBothSidesAreShort_BothAreNamed()
    {
        string? problem = SoundCardEnumerator.ChannelPairProblem(
            Input("In", 2), 2, Output("Out", 2), 2);

        Assert.Equal(
            "Channels 3/4 need 4 input channels and 'In' has 2, and channels 3/4 need 4 output channels and 'Out' has 2.",
            problem);
    }

    // ---- the host-API inventory ----------------------------------------------------------------

    /// <summary>
    /// Every host API the library carries is reported, <b>those that found nothing included</b>.
    ///
    /// <para>This is the regression stated as such. Until 2026-11-07 nothing above the interop
    /// could see the host-API list: the inventory carried only the two device lists, and the log
    /// line grouped <i>those</i> by host API name — so an API with no usable devices appeared
    /// nowhere at all. A user reported that his interface "was not discovered and thus I could not
    /// choose it", behind an ASIO4ALL driver which this app supports and does not filter, and
    /// neither the screen nor the log could say which of three states the machine was in: no ASIO
    /// in the build, ASIO with no driver answering, or a driver answering and reporting no
    /// channels. They need three different answers and looked identical.</para>
    /// </summary>
    [Fact]
    public void HostApiOptions_ReportAnApiThatFoundNothing()
    {
        using var fake = new FakePortAudio();
        fake.AddHostApi("ASIO", SoundCardHostApiKind.Asio);
        var wasapi = fake.AddHostApi("Windows WASAPI", SoundCardHostApiKind.Wasapi);
        fake.AddDevice(wasapi, "In 1-2 (MOTU M Series)", inputs: 2, outputs: 0);

        var options = new SoundCardEnumerator(fake).HostApiOptions();

        SoundCardHostApiOption asio = Assert.Single(options, o => o.Kind == "Asio");
        Assert.Equal(0, asio.DeviceCount);
        Assert.Equal(0, asio.InputCount);
        Assert.Equal(0, asio.OutputCount);
        Assert.Equal("Trusted", asio.Trust);
    }

    /// <summary>
    /// A driver that starts and reports <b>no channels</b> is counted as a device, and separately
    /// as no inputs and no outputs — which is the whole reason <c>DeviceCount</c> is not simply the
    /// two directions added up. That state is ASIO4ALL with nothing ticked in its own panel, or a
    /// card another application is holding, and it is the one of the three a user can fix in a
    /// minute once told. It is dropped from both of the inventory's device lists, so without this
    /// it is indistinguishable from the driver not being installed.
    /// </summary>
    [Fact]
    public void HostApiOptions_CountADriverThatReportsNoChannels_AsADeviceAllTheSame()
    {
        using var fake = new FakePortAudio();
        var asio = fake.AddHostApi("ASIO", SoundCardHostApiKind.Asio);
        fake.AddDevice(asio, "ASIO4ALL v2", inputs: 0, outputs: 0);

        SoundCardHostApiOption option = Assert.Single(new SoundCardEnumerator(fake).HostApiOptions());

        Assert.Equal(1, option.DeviceCount);
        Assert.Equal(0, option.InputCount);
        Assert.Equal(0, option.OutputCount);
        Assert.Equal("ASIO 1 dev/0 in/0 out", option.Describe());
    }

    /// <summary>
    /// A device counts toward each direction it carries, so the measured Windows topology — one
    /// interface split into separate input and output endpoints — reads as three devices, two in,
    /// one out, rather than as three of each.
    /// </summary>
    [Fact]
    public void HostApiOptions_CountEachDirectionSeparately()
    {
        using FakePortAudio fake = FakePortAudio.WithASplitInterface();

        var options = new SoundCardEnumerator(fake).HostApiOptions();

        SoundCardHostApiOption wasapi = Assert.Single(options, o => o.Kind == "Wasapi");
        Assert.Equal(3, wasapi.DeviceCount);
        Assert.Equal(2, wasapi.InputCount);
        Assert.Equal(1, wasapi.OutputCount);
        Assert.Equal("Windows WASAPI 3 dev/2 in/1 out", wasapi.Describe());
    }

    /// <summary>A full-duplex device counts once as a device and once in each direction.</summary>
    [Fact]
    public void HostApiOptions_CountAFullDuplexDeviceOnce()
    {
        using var fake = new FakePortAudio();
        var asio = fake.AddHostApi("ASIO", SoundCardHostApiKind.Asio);
        fake.AddDevice(asio, "MOTU M Series", inputs: 8, outputs: 4);

        SoundCardHostApiOption option = Assert.Single(new SoundCardEnumerator(fake).HostApiOptions());

        Assert.Equal(1, option.DeviceCount);
        Assert.Equal(1, option.InputCount);
        Assert.Equal(1, option.OutputCount);
    }

    /// <summary>
    /// Ordered as the picker offers them — the host APIs that reach the hardware first — so the log
    /// line reads in the same order as the dropdown it is being compared against.
    /// </summary>
    [Fact]
    public void HostApiOptions_AreOrderedAsThePickerOffersThem()
    {
        using var fake = new FakePortAudio();
        fake.AddHostApi("MME", SoundCardHostApiKind.Mme);
        fake.AddHostApi("Windows WASAPI", SoundCardHostApiKind.Wasapi);
        fake.AddHostApi("ASIO", SoundCardHostApiKind.Asio);

        Assert.Equal(
            ["Asio", "Wasapi", "Mme"],
            new SoundCardEnumerator(fake).HostApiOptions().Select(o => o.Kind));
    }

    // ---- what the log writes about a device ----------------------------------------------------

    private static SoundCardOption Option(
        string hostApi, string name, string? card = null, int inputs = 2, int outputs = 0,
        bool duplex = false, int preference = 4) =>
        new(hostApi, name, card ?? name, "Wasapi", inputs, outputs, duplex, "Trusted", preference,
            HasControlPanel: false);

    /// <summary>
    /// The host API, the device, its channel counts each way, and the card it belongs to where that
    /// is not simply its own name — which together are what the picker is given, and therefore the
    /// only way a log can say why a card was or was not offered.
    /// </summary>
    [Fact]
    public void Describe_NamesTheHostApiTheDeviceAndItsChannels()
    {
        Assert.Equal(
            "Windows WASAPI 'Microfoon (2- E-MU Tracker Pre)' -> card '2- E-MU Tracker Pre' 2/0 ch",
            Option("Windows WASAPI", "Microfoon (2- E-MU Tracker Pre)", "2- E-MU Tracker Pre").Describe());

        Assert.Equal(
            "ASIO 'MOTU M Series' 8/4 ch, full duplex",
            Option("ASIO", "MOTU M Series", inputs: 8, outputs: 4, duplex: true, preference: 0).Describe());
    }

    /// <summary>
    /// Under ASIO and WDM-KS the device <i>is</i> the card, so the card is left out rather than
    /// repeated: every line of those two host APIs would otherwise carry the same string twice.
    /// </summary>
    [Fact]
    public void Describe_NamesTheCardOnlyWhereItDiffersFromTheDeviceName()
    {
        Assert.DoesNotContain("card", Option("ASIO", "ASIO4ALL v2", preference: 0).Describe());
        Assert.Contains("card", Option("MME", "In 1-2 (MOTU M Series)", "MOTU M Series").Describe());
    }

    /// <summary>
    /// The regression this is stated as: the two lists are two views of one device table, so a
    /// full-duplex device is in both — and the log line that added their lengths reported this
    /// bench's single MOTU under ASIO as two devices.
    /// </summary>
    [Fact]
    public void DistinctDevices_CountAFullDuplexDeviceOnce()
    {
        SoundCardOption motu = Option("ASIO", "MOTU M Series", inputs: 8, outputs: 4, duplex: true, preference: 0);
        var inventory = new SoundCardInventory(
            Available: true, Version: "PortAudio", HostApis: [], Inputs: [motu], Outputs: [motu]);

        Assert.Equal(["MOTU M Series"], inventory.DistinctDevices().Select(o => o.Name));
    }

    /// <summary>
    /// One name under two host APIs is two devices, and they are both kept: that pair is precisely
    /// what the picker's collapse has to choose between, so a log missing one of them cannot say
    /// why it chose as it did.
    /// </summary>
    [Fact]
    public void DistinctDevices_KeepOneNameUnderTwoHostApis()
    {
        var inventory = new SoundCardInventory(
            Available: true, Version: "PortAudio", HostApis: [],
            Inputs:
            [
                Option("Windows WASAPI", "E-MU Tracker Pre USB"),
                Option("ASIO", "E-MU Tracker Pre USB", preference: 0),
            ],
            Outputs: []);

        Assert.Equal(
            ["ASIO", "Windows WASAPI"],
            inventory.DistinctDevices().Select(o => o.HostApi));
    }

    // ---- which pairings cannot be opened at all --------------------------------------------

    private static SoundCardDeviceDescriptor Asio(string name, int inputs, int outputs, int index) =>
        new(index, name, 0, "ASIO", SoundCardHostApiKind.Asio, inputs, outputs, 0.01, 0.01, 48_000.0);

    /// <summary>
    /// The bench of 2026-11-12: a MOTU M4 input and a Topping D50 III output, both under ASIO.
    /// PortAudio's ASIO backend refuses any pair whose devices differ, so every rate came back
    /// refused and the dialog blamed the rates.
    /// </summary>
    [Fact]
    public void TwoAsioDrivers_CannotBeCombined_AndTheMessageSaysSoRatherThanBlamingTheRates()
    {
        var problem = SoundCardEnumerator.DuplexCombinationProblem(
            Asio("MOTU M Series", inputs: 4, outputs: 4, index: 0),
            Asio("Topping USB Audio Device", inputs: 0, outputs: 2, index: 1));

        Assert.NotNull(problem);
        Assert.Contains("MOTU M Series", problem);
        Assert.Contains("Topping USB Audio Device", problem);
        // It has to name a way out, and the way out is not "same host API" -- which is what the
        // advice it replaced said, to a bench that was already on one.
        Assert.Contains("WASAPI", problem);
        Assert.DoesNotContain("sample rate the", problem);
    }

    /// <summary>One ASIO driver carrying both directions is the case ASIO exists for.</summary>
    [Fact]
    public void OneAsioDeviceUsedBothWays_IsFine()
    {
        var duplex = Asio("MOTU M Series", inputs: 4, outputs: 4, index: 0);

        Assert.Null(SoundCardEnumerator.DuplexCombinationProblem(duplex, duplex));
    }

    /// <summary>
    /// Two cards under any other host API is the configuration this whole separate-devices feature
    /// is for, and must not be refused — the restriction belongs to ASIO alone.
    /// </summary>
    [Theory]
    [InlineData(SoundCardHostApiKind.Wasapi)]
    [InlineData(SoundCardHostApiKind.WdmKs)]
    [InlineData(SoundCardHostApiKind.Mme)]
    public void TwoDevicesUnderAnyOtherHostApi_AreNotRefused(SoundCardHostApiKind kind)
    {
        var input = Device("In (Card A)", inputs: 2, outputs: 0, kind: kind, index: 0);
        var output = Device("Out (Card B)", inputs: 0, outputs: 2, kind: kind, index: 1);

        Assert.Null(SoundCardEnumerator.DuplexCombinationProblem(input, output));
    }

    /// <summary>
    /// A mixed pair is refused by PortAudio for a different reason — a stream cannot span two host
    /// APIs at all — which this rule deliberately does not claim to cover: it is about ASIO's own
    /// per-driver restriction, and a message naming the wrong cause is what it exists to stop.
    /// </summary>
    [Fact]
    public void AOneDirectionBench_HasNoCombinationToRefuse()
    {
        var only = Asio("MOTU M Series", inputs: 4, outputs: 4, index: 0);

        Assert.Null(SoundCardEnumerator.DuplexCombinationProblem(only, null));
        Assert.Null(SoundCardEnumerator.DuplexCombinationProblem(null, only));
        Assert.Null(SoundCardEnumerator.DuplexCombinationProblem(null, null));
    }

    [Fact]
    public void TwoAsioCards_CannotBeTheTwoHalvesOfASplit_AndOnlyOneHasToMove()
    {
        // A harder limit than the duplex one and a different fix: the ASIO SDK has no notion of
        // more than one driver open per process, so no threading arrangement reaches it -- but a
        // split only needs *one* of the two moved, where a duplex stream needs both.
        string? problem = SoundCardSettings.SplitCombinationProblem(
            "ASIO", "MOTU M Series", "ASIO", "Topping USB Audio Device");

        Assert.NotNull(problem);
        Assert.Contains("one driver open per process", problem);
        Assert.Contains("MOTU M Series", problem);
        Assert.Contains("Topping USB Audio Device", problem);
        Assert.Contains("WASAPI", problem);
    }

    [Fact]
    public void OneAsioCardAgainstAnotherHostApi_IsTheWholePointAndIsAllowed()
    {
        // The pairing one duplex stream cannot express at all, in either order.
        Assert.Null(SoundCardSettings.SplitCombinationProblem(
            "ASIO", "MOTU M Series", "Windows WASAPI", "Luidsprekers (TOPPING USB DAC)"));
        Assert.Null(SoundCardSettings.SplitCombinationProblem(
            "Windows WASAPI", "In 1-2 (MOTU M Series)", "ASIO", "Topping USB Audio Device"));
        Assert.Null(SoundCardSettings.SplitCombinationProblem(
            "Windows WASAPI", "In 1-2 (A)", "Windows WASAPI", "Out 1-2 (B)"));
    }

    [Fact]
    public void OneAsioCardOnBothHalves_IsNamedAsASoundCardBenchRatherThanASplit()
    {
        // One interface is one clock, which is the thing a split is not for.
        string? problem = SoundCardSettings.SplitCombinationProblem(
            "ASIO", "MOTU M Series", "ASIO", "MOTU M Series");

        Assert.NotNull(problem);
        Assert.Contains("one interface", problem);
        Assert.Contains("Sound card", problem);
    }
}
