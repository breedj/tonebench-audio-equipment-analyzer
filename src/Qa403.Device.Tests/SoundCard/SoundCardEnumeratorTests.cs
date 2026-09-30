using Qa403.Device.SoundCard;

namespace Qa403.Device.Tests.SoundCard;

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

    [Fact]
    public void Pairing_OfTwoDifferentCards_IsUnknown()
    {
        var input = Device("In 1-2 (MOTU M Series)", inputs: 2, outputs: 0, index: 0);
        var output = Device("Speaker (Realtek(R) Audio)", inputs: 0, outputs: 2, index: 1);

        Assert.Equal(SoundCardPairing.Unknown, SoundCardEnumerator.Pairing(input, output));
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
        Assert.Equal([48_000.0, 96_000.0, 192_000.0], enumerator.SupportedRatesHz(input, output, exclusive: true));
    }

    /// <summary>
    /// Only the three the rest of the app is built around. A card offering 44.1 kHz does not get to
    /// put it in front of a user, because every FFT-size table and chirp period downstream assumes
    /// one of these three.
    /// </summary>
    [Fact]
    public void SupportedRates_AreOnlyEverTheThreeTheAppIsBuiltAround()
    {
        var fake = new FakePortAudio();
        var api = fake.AddHostApi("Windows WASAPI", SoundCardHostApiKind.Wasapi);
        var input = fake.AddDevice(api, "In (Card)", inputs: 2, outputs: 0);
        var output = fake.AddDevice(api, "Out (Card)", inputs: 0, outputs: 2);

        var rates = new SoundCardEnumerator(fake).SupportedRatesHz(input, output, exclusive: true);

        Assert.Equal(SoundCardEnumerator.CandidateRatesHz, rates);
        Assert.DoesNotContain(44_100.0, rates);
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
}
